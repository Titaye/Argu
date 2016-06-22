﻿[<AutoOpen>]
module internal Argu.PreCompute

open System
open System.Reflection
open System.Collections.Generic
open System.Text.RegularExpressions

open FSharp.Reflection

let defaultHelpParam = "help"
let defaultHelpDescription = "display this list of options."

let getDefaultHelpParam (t : Type) =
    let prefixString =
        match t.TryGetAttribute<CliPrefixAttribute>() with
        | None -> CliPrefix.DoubleDash
        | Some pf -> pf.Prefix

    prefixString + defaultHelpParam

/// construct a CLI param from UCI name
let generateOptionName (uci : UnionCaseInfo) =
    let prefixString = 
        match uci.TryGetAttribute<CliPrefixAttribute>(true) with
        | None -> CliPrefix.DoubleDash
        | Some pf -> pf.Prefix

    prefixString + uci.Name.ToLower().Replace('_','-')

/// construct an App.Config param from UCI name
let generateAppSettingsName (uci : UnionCaseInfo) =
    uci.Name.ToLower().Replace('_',' ')

/// get CL arguments from environment
let getEnvironmentCommandLineArgs () =
    match System.Environment.GetCommandLineArgs() with
    | [||] -> [||]
    | args -> args.[1..]

/// Creates a primitive field parser from given parser/unparser lambdas
let mkPrimitiveParser (name : string) (parser : string -> 'T) (unparser : 'T -> string) (label : string option) =
    {
        Name = name
        Label = label
        Type = typeof<'T>
        Parser = fun x -> parser x :> obj
        UnParser = fun o -> unparser (o :?> 'T)
    }

let primitiveParsers =
    let mkParser name (pars : string -> 'a) unpars = typeof<'a>, mkPrimitiveParser name pars unpars in
    dict [|
        mkParser "bool" Boolean.Parse (fun b -> if b then "true" else "false")
        mkParser "byte" Byte.Parse string
        mkParser "sbyte" SByte.Parse string

        mkParser "int16" Int16.Parse string
        mkParser "int" Int32.Parse string
        mkParser "int64" Int64.Parse string
        mkParser "uint16" UInt16.Parse string
        mkParser "uint" UInt32.Parse string
        mkParser "uint64" UInt64.Parse string

        mkParser "char" Char.Parse string
        mkParser "string" id id

        mkParser "float" Single.Parse string
        mkParser "float" Double.Parse string
        mkParser "decimal" Decimal.Parse string
#if !NET35
        mkParser "bigint" System.Numerics.BigInteger.Parse string
#endif
        mkParser "guid" Guid string
        mkParser "base64" Convert.FromBase64String Convert.ToBase64String
    |]


let (|UnionParseResult|_|) (t : Type) =
    if t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<ParseResult<_>> then
        Some(t.GetGenericArguments().[0])
    else None

let private validCliParamRegex = new Regex(@"\S+", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)
let validateCliParam (name : string) =
    if name = null || not <| validCliParamRegex.IsMatch name then
        failwithf "Argu: CLI parameter '%s' contains invalid characters." name
    

/// extracts the subcommand argument hierarchy for given UnionArgInfo
let getHierarchy (uai : UnionArgInfo) =
    let rec aux acc (uai : UnionArgInfo) =
        match uai.TryGetParent () with
        | None -> acc
        | Some ucai -> aux (ucai :: acc) (ucai.GetParent())

    aux [] uai

/// generate argument parsing schema from given UnionCaseInfo
let rec private preComputeUnionCaseArgInfo (stack : Type list) (helpParam : HelpParam option)
                                            (getParent : unit -> UnionArgInfo)
                                            (uci : UnionCaseInfo) : UnionCaseArgInfo =

    let fields = uci.GetFields()
    let types = fields |> Array.map (fun f -> f.PropertyType)

    let caseCtor = FSharpValue.PreComputeUnionConstructor(uci, bindingFlags = allBindings)

    // use ref cell for late binding of parent argInfo
    let current = ref None
    let tryGetCurrent = fun () -> !current

    /// create a dummy instance for the current union case
    let usageString = 
        let dummyFields = types |> Array.map Unchecked.UntypedDefaultOf
        let dummy = caseCtor dummyFields :?> IArgParserTemplate
        dummy.Usage
        
    let commandLineArgs =
        if uci.ContainsAttribute<NoCommandLineAttribute> (true) then []
        else
            let defaultName =
                match uci.TryGetAttribute<CustomCommandLineAttribute> () with 
                | None -> generateOptionName uci
                | Some attr -> attr.Name

            let altNames =
                uci.GetAttributes<AltCommandLineAttribute> ()
                |> Seq.collect (fun attr -> attr.Names)
                |> Seq.toList

            let cliNames = defaultName :: altNames

            for name in cliNames do validateCliParam name

            cliNames

    let appSettingsName =
        if uci.ContainsAttribute<NoAppSettingsAttribute> (true) then None
        else
            match uci.TryGetAttribute<CustomAppSettingsAttribute> () with
            | None -> Some <| generateAppSettingsName uci
            // take last registered attribute
            | Some attr when not <| String.IsNullOrWhiteSpace attr.Name -> Some attr.Name
            | Some attr -> failwithf "Argu: AppSettings parameter '%s' contains invalid characters." attr.Name

    /// gets the default name of the argument
    let defaultName =
        match commandLineArgs with
        | h :: _ -> h
        | _ when Option.isSome appSettingsName -> appSettingsName.Value
        | _ -> failwithf "Argu: parameter '%s' needs to have at least one parse source." uci.Name

    let printLabels = uci.ContainsAttribute<PrintLabelsAttribute> (true)
    let appSettingsCSV = uci.ContainsAttribute<ParseCSVAttribute> ()
    let mandatory = uci.ContainsAttribute<MandatoryAttribute> (true)
    let gatherAll = uci.ContainsAttribute<GatherAllSourcesAttribute> ()
    let isRest = uci.ContainsAttribute<RestAttribute> ()
    let isHidden = uci.ContainsAttribute<HiddenAttribute> ()
    let isEqualsAssignment = 
        if uci.ContainsAttribute<EqualsAssignmentAttribute> (true) then
            if types.Length <> 1 then
                failwithf "Argu: Parameter '%s' has EqualsAssignment attribute but has arity <> 1." uci.Name
            elif isRest then
                failwithf "Argu: Parameter '%s' contains incompatible attributes 'EqualsAssignment' and 'Rest'." uci.Name
            true
        else
            false

    let parsers =
        let getParser (p : PropertyInfo) =
            let label = if printLabels then Some p.Name else None
            let ok, f = primitiveParsers.TryGetValue p.PropertyType
            if ok then f label
            else
                failwithf "Argu: template contains unsupported field of type '%O'." p.PropertyType

        match types with
        | [|UnionParseResult prt|] -> 
            if isEqualsAssignment then
                failwithf "Argu: EqualsAssignment in '%s' not supported for nested union cases." uci.Name

            let argInfo = preComputeUnionArgInfoInner stack helpParam tryGetCurrent prt 
            let shape = ShapeArgumentTemplate.FromType prt
            NestedUnion(shape, argInfo)

        | _ ->  Array.map getParser fields |> Primitives

    let fieldCtor = lazy(
        match types.Length with
        | 0 -> None
        | 1 -> Some(fun (o:obj[]) -> o.[0])
        | _ ->
            let tupleType = FSharpType.MakeTupleType types
            let ctor = FSharpValue.PreComputeTupleConstructor tupleType
            Some ctor)

    let fieldReader = lazy(FSharpValue.PreComputeUnionReader(uci, bindingFlags = allBindings))

    let first = uci.ContainsAttribute<FirstAttribute> ()

    if appSettingsCSV && fields.Length <> 1 then 
        failwith "Argu: CSV attribute is only compatible with branches of unary fields." 

    let uai = {
        UnionCaseInfo = uci
        CaseCtor = caseCtor
        FieldReader = fieldReader
        FieldCtor = fieldCtor
        Name = defaultName
        GetParent = getParent
        CommandLineNames = commandLineArgs
        AppSettingsName = appSettingsName
        Usage = usageString
        FieldParsers = parsers
        AppSettingsCSV = appSettingsCSV
        Mandatory = mandatory
        PrintLabels = printLabels
        GatherAllSources = gatherAll
        IsRest = isRest
        IsFirst = first
        IsEqualsAssignment = isEqualsAssignment
        Hidden = isHidden
    }

    current := Some uai // assign result to children
    uai

and private preComputeUnionArgInfoInner (stack : Type list) (helpParam : HelpParam option) (tryGetParent : unit -> UnionCaseArgInfo option) (t : Type) : UnionArgInfo =
    if not <| FSharpType.IsUnion(t, bindingFlags = allBindings) then
        invalidArg t.Name "Argu: template type is not F# DU."
    elif stack |> List.exists ((=) t) then
        invalidArg t.Name "Argu: template type implements unsupported recursive pattern."
    elif t.IsGenericType then
        invalidArg t.Name "Argu: template type is generic which is not supported."

    let helpParam =
        match helpParam with
        | Some hp -> hp // always inherit help schema from parent union
        | None ->
            let helpSwitches =
                match t.TryGetAttribute<HelpFlagsAttribute>() with
                | None -> [getDefaultHelpParam t]
                | Some hf -> 
                    for f in hf.Names do validateCliParam f
                    Array.toList hf.Names

            let description = 
                match t.TryGetAttribute<HelpDescriptionAttribute> () with
                | None -> defaultHelpDescription
                | Some attr -> attr.Description

            { Flags = helpSwitches ; Description = description }


    // use ref cell for late binding of parent argInfo
    let current = ref Unchecked.defaultof<_>
    let getCurrent = fun () -> !current

    let caseInfo =
        FSharpType.GetUnionCases(t, bindingFlags = allBindings)
        |> Seq.map (preComputeUnionCaseArgInfo (t :: stack) (Some helpParam) getCurrent)
        |> Seq.sortBy (fun a -> a.Tag)
        |> Seq.toArray

    // check for conflicting CLI identifiers
    let cliConflicts =
        caseInfo
        |> Seq.collect (fun arg -> arg.CommandLineNames |> Seq.map (fun name -> arg, name))
        |> Seq.map (fun ((arg, name) as t) ->
            if helpParam.Flags |> List.exists ((=) name) then
                failwithf "Argu: Parameter '%O' using CLI identifier '%s' which is reserved for help params." arg.UnionCaseInfo name
            t)
        |> Seq.groupBy snd
        |> Seq.map (fun (name, args) -> name, args |> Seq.map fst |> Seq.toArray)
        |> Seq.filter (fun (_,args) -> args.Length > 1)
        |> Seq.sortBy (fun (_,args) -> -args.Length)
        |> Seq.toArray

    if cliConflicts.Length > 0 then
        let id, cs = cliConflicts.[0]
        let msg = sprintf "Argu: Parameters '%O' and '%O' using conflicting CLI identifier '%s'." cs.[0].UnionCaseInfo cs.[1].UnionCaseInfo id
        raise <| new ArgumentException(msg)

    // check for conflicting CLI identifiers
    let appSettingsConflicts =
        caseInfo
        |> Seq.choose(fun arg -> arg.AppSettingsName |> Option.map (fun name -> arg, name))
        |> Seq.groupBy snd
        |> Seq.map (fun (name, args) -> name, args |> Seq.map fst |> Seq.toArray)
        |> Seq.filter (fun (_,args) -> args.Length > 1)
        |> Seq.sortBy (fun (_,args) -> -args.Length)
        |> Seq.toArray

    if appSettingsConflicts.Length > 0 then
        let id, cs = appSettingsConflicts.[0]
        let msg = sprintf "Argu: Parameters '%O' and '%O' using conflicting AppSettings identifier '%s'." cs.[0].UnionCaseInfo cs.[1].UnionCaseInfo id
        raise <| new ArgumentException(msg)

    // recognizes and extracts grouped switches
    // e.g. -efx --> -e -f -x
    let groupedSwitchExtractor = lazy(
        let chars =
            caseInfo
            |> Seq.collect (fun c -> c.CommandLineNames) 
            |> Seq.filter (fun name -> name.Length = 2 && name.[0] = '-' && Char.IsLetterOrDigit name.[1])
            |> Seq.map (fun name -> name.[1])
            |> Seq.toArray
            |> String

        if chars.Length = 0 then (fun _ -> [||]) else

        let regex = new Regex(sprintf "^-[%s]+$" chars, RegexOptions.Compiled)

        fun (arg : string) ->
            if not <| regex.IsMatch arg then [||] 
            else Array.init (arg.Length - 1) (fun i -> sprintf "-%c" arg.[i + 1]))

    let tagReader = lazy(FSharpValue.PreComputeUnionTagReader(t, bindingFlags = allBindings))

    let appSettingsIndex = lazy(
        caseInfo
        |> Seq.choose (fun cs -> match cs.AppSettingsName with Some name -> Some(name, cs) | None -> None)
        |> dict)

    let cliIndex = lazy(
        caseInfo
        |> Seq.collect (fun cs -> cs.CommandLineNames |> Seq.map (fun name -> name, cs))
        |> dict)

    let result = {
        Type = t
        TryGetParent = tryGetParent
        Cases = caseInfo
        TagReader = tagReader
        GroupedSwitchExtractor = groupedSwitchExtractor
        AppSettingsParamIndex = appSettingsIndex
        HelpParam = helpParam
        CliParamIndex = cliIndex
    }

    current := result // assign result to children
    result

and preComputeUnionArgInfo<'Template when 'Template :> IArgParserTemplate> () = 
    preComputeUnionArgInfoInner [] None (fun () -> None) typeof<'Template>