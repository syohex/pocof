namespace pocof

#if DEBUG

[<AutoOpen>]
module PocofDebug =
    open System
    open System.IO
    open System.Runtime.CompilerServices
    open System.Runtime.InteropServices

    let lockObj = new obj ()

    let logPath = "./debug.log"

    [<AbstractClass; Sealed>]
    type Logger =
        static member logFile
            (
                res,
                [<Optional; DefaultParameterValue(""); CallerMemberName>] caller: string,
                [<CallerFilePath; Optional; DefaultParameterValue("")>] path: string,
                [<CallerLineNumber; Optional; DefaultParameterValue(0)>] line: int
            ) =

            // NOTE: lock to avoid another process error when dotnet test.
            lock lockObj (fun () ->
                use sw = new StreamWriter(logPath, true)

                res
                |> List.iter (
                    fprintfn
                        sw
                        "[%s] %s at %d %s <%A>"
                        (DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz"))
                        path
                        line
                        caller
                ))
#endif

[<AutoOpen>]
module LanguageExtension =
    open System

    type String with
        static member inline lower(s: string) = s.ToLower()
        static member inline upper(s: string) = s.ToUpper()
        static member inline startsWith (value: string) (s: string) = s.StartsWith(value)
        static member inline split (separator: string) (s: string) = s.Split(separator.ToCharArray())
        static member inline equals (opt: StringComparison) (value: string) (s: string) = s.Equals(value, opt)
        static member inline trim(s: string) = s.Trim()
        static member inline replace (oldValue: string) (newValue: string) (s: string) = s.Replace(oldValue, newValue)

    let inline swap (l, r) = (r, l)
    let inline alwaysTrue _ = true

module PocofData =
    open System
    open System.Management.Automation
    open System.Collections
    open Microsoft.FSharp.Reflection

    type Entry =
        | Obj of PSObject
        | Dict of DictionaryEntry

    let unwrap (entries: Entry list) =
        entries
        |> List.map (function
            | Dict (dct) -> dct :> obj
            | Obj (o) -> o)


    let inline private tryFromStringExcludes<'a> (excludes: Set<string>) s =
        let name = String.lower s
        let aType = typeof<'a>

        match FSharpType.GetUnionCases aType
              |> Seq.filter (fun u -> Set.contains u.Name excludes |> not)
              |> Seq.tryFind (fun u -> u.Name |> String.lower = name)
            with
        | Some u -> Ok <| (FSharpValue.MakeUnion(u, [||]) :?> 'a)
        | _ -> Error <| $"Unknown %s{aType.Name} '%s{s}'."

    let inline private fromString<'a> s =
        let name = String.lower s
        let aType = typeof<'a>

        match FSharpType.GetUnionCases aType
              |> Seq.tryFind (fun u -> u.Name |> String.lower = name)
            with
        | Some u -> FSharpValue.MakeUnion(u, [||]) :?> 'a
        | _ -> failwithf $"Unknown %s{aType.Name} '%s{s}'."

    let inline private toString (x: 'a) =
        match FSharpValue.GetUnionFields(x, typeof<'a>) with
        | case, _ -> case.Name

    let (|Prefix|_|) (p: string) (s: string) =
        match String.startsWith p s with
        | true -> Some s.[1..]
        | _ -> None

    type Action =
        | Noop
        | Cancel
        | Finish
        // move.
        | BackwardChar
        | ForwardChar
        | BeginningOfLine
        | EndOfLine
        // edit query.
        | AddQuery of string
        | DeleteBackwardChar
        | DeleteForwardChar
        | KillBeginningOfLine
        | KillEndOfLine
        // toggle options.
        | RotateMatcher
        | RotateOperator
        | ToggleCaseSensitive
        | ToggleInvertFilter
        | ToggleSuppressProperties
        // move selection.
        | SelectUp
        | SelectDown
        | ScrollPageUp
        | ScrollPageDown
        // autocomplete
        | CompleteProperty
        static member fromString =
            tryFromStringExcludes<Action>
            <| set [ "AddQuery" ]

    type Matcher =
        | EQ
        | LIKE
        | MATCH
        static member fromString = fromString<Matcher>
        override __.ToString() = toString __ |> String.lower

    type Operator =
        | AND
        | OR
        | NONE
        static member fromString = fromString<Operator>
        override __.ToString() = toString __ |> String.lower

    type Layout =
        | TopDown
        | BottomUp
        static member fromString = fromString<Layout>

    type PropertySearch =
        | NoSearch
        | Search of string
        | Rotate of string * int * string list

    type Refresh =
        | Required
        | NotRequired
        static member ofBool =
            function
            | true -> Required
            | _ -> NotRequired

    type KeyPattern = { Modifier: int; Key: ConsoleKey }

    type InternalConfig =
        { Prompt: string
          Layout: Layout
          Keymaps: Map<KeyPattern, Action>
          NotInteractive: bool }

    type QueryState =
        { Query: string
          Cursor: int
          WindowBeginningX: int
          WindowWidth: int }

    module QueryState =
        let addQuery (state: QueryState) (query: string) =
            { state with
                Query = state.Query.Insert(state.Cursor, query)
                Cursor = state.Cursor + query.Length }

        let moveCursor (state: QueryState) (step: int) =
            let x =
                match state.Cursor + step with
                | x when x < 0 -> 0
                | x when x > state.Query.Length -> state.Query.Length
                | _ -> state.Cursor + step

            { state with
                Cursor = x
                WindowBeginningX = 0 } // TODO: adjust with windows width.

        let setCursor (state: QueryState) (x: int) =
            { state with
                Cursor = x
                WindowBeginningX = 0 } // TODO: adjust with windows width.

        let backspaceQuery (state: QueryState) (size: int) =
            let cursor, size =
                match state.Query.Length - state.Cursor with
                | x when x < 0 -> state.Query.Length, size + x
                | _ -> state.Cursor, size

            let i, c =
                match cursor - size with
                | x when x < 0 -> 0, state.Cursor
                | x -> x, size

            { state with
                Query = state.Query.Remove(i, c)
                Cursor = state.Cursor - size
                WindowBeginningX = 0 } // TODO: adjust with windows width.

        let deleteQuery (state: QueryState) (size: int) =
            match state.Query.Length - state.Cursor with
            | x when x < 0 -> { state with Cursor = state.Query.Length }
            | _ ->
                { state with
                    Query = state.Query.Remove(state.Cursor, size)
                    WindowBeginningX = 0 } // TODO: adjust with windows width.

        let getCurrentProperty (state: QueryState) =
            let s =
                state.Query.[.. state.Cursor - 1]
                |> String.split " "
                |> Seq.last

#if DEBUG
            Logger.logFile [ $"query '{state.Query}' x '{state.Cursor}' string '{s}'" ]
#endif

            match s with
            | Prefix ":" p -> Search p
            | _ -> NoSearch

    type QueryCondition =
        { Matcher: Matcher
          Operator: Operator
          CaseSensitive: bool
          Invert: bool }
        override __.ToString() =
            List.append
            <| match __.Matcher, __.CaseSensitive, __.Invert with
               | EQ, true, true -> [ "cne" ]
               | EQ, false, true -> [ "ne" ]
               | m, true, true -> [ "notc"; string m ]
               | m, true, false -> [ "c"; string m ]
               | m, false, true -> [ "not"; string m ]
               | m, false, false -> [ string m ]
            <| [ " "; string __.Operator ]
            |> String.concat ""

    type InternalState =
        { QueryState: QueryState
          QueryCondition: QueryCondition
          PropertySearch: PropertySearch
          Notification: string
          SuppressProperties: bool
          Properties: string list
          Refresh: Refresh }

    let refresh (state: InternalState) = { state with Refresh = Required }

    let noRefresh (state: InternalState) = { state with Refresh = NotRequired }

    type Position = { Y: int; Height: int }

    type IncomingParameters =
        { Query: string
          Matcher: string
          Operator: string
          CaseSensitive: bool
          InvertQuery: bool
          NotInteractive: bool
          SuppressProperties: bool
          Prompt: string
          Layout: string
          Keymaps: Map<KeyPattern, Action>
          Properties: string list
          ConsoleWidth: int
          ConsoleHeight: int }

    let initConfig (p: IncomingParameters) =
        let qs =
            { Query = p.Query
              Cursor = p.Query.Length
              WindowBeginningX = 0 // TODO: adjust with query size.
              WindowWidth = p.ConsoleWidth } // TODO: adjust with query condition size.

        { Prompt = p.Prompt
          Layout = Layout.fromString p.Layout
          Keymaps = p.Keymaps
          NotInteractive = p.NotInteractive },
        { QueryState =
            { Query = p.Query
              Cursor = p.Query.Length
              WindowBeginningX = 0 // TODO: adjust with query size.
              WindowWidth = p.ConsoleWidth } // TODO: adjust with query condition size.
          QueryCondition =
            { Matcher = Matcher.fromString p.Matcher
              Operator = Operator.fromString p.Operator
              CaseSensitive = p.CaseSensitive
              Invert = p.InvertQuery }
          PropertySearch = QueryState.getCurrentProperty qs
          Notification = ""
          SuppressProperties = p.SuppressProperties
          Properties = p.Properties
          Refresh = Required },
        { Y = 0; Height = p.ConsoleHeight }
