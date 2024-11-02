namespace Pocof

open System

open Data
open Query

module Handle =
    type QueryContext = Query.QueryContext

    let private addQuery (state: InternalState) (pos: Position) (context: QueryContext) (query: string) =
        let qs = QueryState.deleteSelection state.QueryState |> QueryState.addQuery query

        let state =
            state
            |> InternalState.updateQueryState qs
            |> InternalState.refresh
            |> InternalState.prepareNotification

        state, pos, context |> QueryContext.prepareQuery state

    let private updateCursor
        (update: QueryState -> int -> QueryState)
        (cursor: int)
        (mode: InputMode)
        (state: InternalState)
        (pos: Position)
        (context: QueryContext)
        =
        let qs = update state.QueryState cursor |> QueryState.setInputMode mode

        state
        |> InternalState.refreshIfTrue (state.QueryState.Cursor <> qs.Cursor)
        |> InternalState.updateQueryState qs,
        pos,
        context

    let private moveCursor = updateCursor QueryState.moveCursor
    let private moveCursorBackwardWith = moveCursor -1
    let private backwardChar = moveCursorBackwardWith InputMode.Input
    let private moveCursorForwardWith (mode: InputMode) (state: InternalState) = moveCursor 1 mode state
    let private forwardChar = moveCursorForwardWith InputMode.Input
    let defaultWordDelimiters = ";:,.[]{}()/\\|!?^&*-=+'\"–—―" // TODO: lift to Cmdlet Option.

    let private isWordDelimiter (wordDelimiters: string) (c: char) =
        Char.IsWhiteSpace c || wordDelimiters.IndexOf(c) >= 0

    [<TailCall>]
    let rec private findCursorOfChar (predicate: char -> bool) (str: char list) (cursor: int) =
        match str with
        | [] -> List.Empty, cursor
        | c :: cs ->
            if predicate c then
                cursor + 1 |> findCursorOfChar predicate cs
            else
                str, cursor

    let private findWordCursor wordDelimiters =
        isWordDelimiter wordDelimiters |> findCursorOfChar

    let rec private findWordDelimiterCursor wordDelimiters =
        isWordDelimiter wordDelimiters >> not |> findCursorOfChar

    let private findBackwardWordCursor (wordDelimiters: string) (query: string) (cursor: int) =
        if String.length query < cursor then
            List.Empty, 0
        else
            let str = query.Substring(0, cursor) |> Seq.rev |> List.ofSeq
            // NOTE: emulate the behavior of the backward-word function in the PSReadLine.
            findWordCursor wordDelimiters str 0 ||> findWordDelimiterCursor wordDelimiters

    let private backwardWord (state: InternalState) =
        let _, i =
            findBackwardWordCursor state.WordDelimiters state.QueryState.Query state.QueryState.Cursor

        moveCursor -i InputMode.Input state

    let private findForwardWordCursor (wordDelimiters: string) (query: string) (cursor: int) =
        if String.length query < cursor then
            List.Empty, 0
        else
            let str = query.Substring(cursor) |> List.ofSeq
            // NOTE: emulate the behavior of the forward-word function in the PSReadLine.
            findWordDelimiterCursor wordDelimiters str 0 ||> findWordCursor wordDelimiters

    let private forwardWord (state: InternalState) =
        let _, i =
            findForwardWordCursor state.WordDelimiters state.QueryState.Query state.QueryState.Cursor

        moveCursor i InputMode.Input state

    let private setCursor = updateCursor QueryState.setCursor
    let private beginningOfLine = setCursor 0 InputMode.Input

    let private endOfLine (state: InternalState) =
        setCursor <| String.length state.QueryState.Query <| InputMode.Input <| state

    [<RequireQualifiedAccess>]
    [<NoComparison>]
    [<NoEquality>]
    [<Struct>]
    type private Direction =
        | Backward
        | Forward

    let private removeChar
        (direction: Direction)
        (size: int)
        (state: InternalState)
        (pos: Position)
        (context: QueryContext)
        =
        match state.QueryState.InputMode with
        | InputMode.Select _ ->
            let qs = QueryState.deleteSelection state.QueryState

            let state =
                state
                |> InternalState.refresh
                |> InternalState.updateQueryState qs
                |> InternalState.prepareNotification

            state, pos, context |> QueryContext.prepareQuery state
        | _ ->
            let limit =
                match direction with
                | Direction.Backward -> 0
                | Direction.Forward -> String.length state.QueryState.Query

            match state.QueryState.Cursor with
            | x when x = limit -> InternalState.noRefresh state, pos, context
            | _ ->
                let qs =
                    match direction with
                    | Direction.Backward -> QueryState.backspaceQuery state.QueryState
                    | Direction.Forward -> QueryState.deleteQuery state.QueryState
                    <| size

                let s =
                    state
                    |> InternalState.refreshIfTrue (
                        state.QueryState.Query <> qs.Query || state.QueryState.Cursor <> qs.Cursor
                    )
                    |> InternalState.updateQueryState qs
                    |> InternalState.prepareNotification

                s, pos, context |> QueryContext.prepareQuery s

    let private deleteBackwardChar = removeChar Direction.Backward 1
    let private deleteForwardChar = removeChar Direction.Forward 1

    let private deleteBackwardWord (state: InternalState) (pos: Position) (context: QueryContext) =
        let _, i =
            findBackwardWordCursor state.WordDelimiters state.QueryState.Query state.QueryState.Cursor

        let state, pos, context, i =
            match state.QueryState.InputMode with
            | InputMode.Input -> (state, pos, context, i)
            | InputMode.Select c ->
                if c > 0 then
                    let mode = max c i |> InputMode.Select

                    let state =
                        InternalState.updateQueryState
                        <| QueryState.setInputMode mode state.QueryState
                        <| state

                    state, pos, context, i // TODO: i has no meaning when the selection is not empty.
                else
                    let selection = state.QueryState.Cursor - c
                    let state, pos, context = setCursor selection InputMode.Input state pos context
                    state, pos, context, i - c

        removeChar Direction.Backward i state pos context

    let private deleteForwardWord (state: InternalState) (pos: Position) (context: QueryContext) =
        let _, i =
            findForwardWordCursor state.WordDelimiters state.QueryState.Query state.QueryState.Cursor

        let state, pos, context, i =
            match state.QueryState.InputMode with
            | InputMode.Input -> (state, pos, context, i)
            | InputMode.Select c ->
                if c < 0 then
                    let mode = min c -i |> InputMode.Select

                    let state =
                        InternalState.updateQueryState
                        <| QueryState.setInputMode mode state.QueryState
                        <| state

                    state, pos, context, i // TODO: i has no meaning when the selection is not empty.
                else
                    let selection = state.QueryState.Cursor - c
                    let state, pos, context = setCursor selection InputMode.Input state pos context
                    state, pos, context, i + c

        removeChar Direction.Forward i state pos context

    let private deleteBackwardInput (state: InternalState) (pos: Position) (context: QueryContext) =
        let state, pos, context =
            match state.QueryState.InputMode with
            | InputMode.Input -> (state, pos, context)
            | InputMode.Select c ->
                let selection = max state.QueryState.Cursor <| state.QueryState.Cursor - c
                let state, pos, context = setCursor selection InputMode.Input state pos context
                let mode = QueryState.getQuerySelection -selection state.QueryState
                setCursor 0 mode state pos context

        removeChar Direction.Backward state.QueryState.Cursor state pos context

    let private deleteForwardInput (state: InternalState) (pos: Position) (context: QueryContext) =
        let queryLength = String.length state.QueryState.Query

        let state, pos, context, beginning =
            match state.QueryState.InputMode with
            | InputMode.Input -> (state, pos, context, state.QueryState.Cursor)
            | InputMode.Select c ->
                let beginning = min state.QueryState.Cursor <| state.QueryState.Cursor - c
                let state, pos, context = setCursor queryLength InputMode.Input state pos context
                let mode = QueryState.getQuerySelection -(queryLength - beginning) state.QueryState
                let state, pos, context = setCursor beginning mode state pos context
                state, pos, context, beginning

        removeChar Direction.Forward (queryLength - beginning) state pos context

    let private selectBackwardChar (state: InternalState) =
        moveCursorBackwardWith
        <| QueryState.getQuerySelection -1 state.QueryState
        <| state

    let private selectForwardChar (state: InternalState) =
        moveCursorForwardWith
        <| QueryState.getQuerySelection 1 state.QueryState
        <| state

    let private selectBackwardWord (state: InternalState) =
        let _, i =
            findBackwardWordCursor state.WordDelimiters state.QueryState.Query state.QueryState.Cursor

        setCursor
        <| state.QueryState.Cursor - i
        <| QueryState.getQuerySelection -i state.QueryState
        <| state

    let private selectForwardWord (state: InternalState) =
        let _, i =
            findForwardWordCursor state.WordDelimiters state.QueryState.Query state.QueryState.Cursor

        setCursor
        <| state.QueryState.Cursor + i
        <| QueryState.getQuerySelection i state.QueryState
        <| state

    let private selectToBeginningOfLine (state: InternalState) (pos: Position) (context: QueryContext) =
        setCursor 0
        <| QueryState.getQuerySelection -state.QueryState.Cursor state.QueryState
        <| state
        <| pos
        <| context

    let private selectToEndOfLine (state: InternalState) (pos: Position) (context: QueryContext) =
        let s = String.length state.QueryState.Query - state.QueryState.Cursor

        setCursor
        <| String.length state.QueryState.Query
        <| QueryState.getQuerySelection s state.QueryState
        <| state
        <| pos
        <| context

    let private selectAll (state: InternalState) (pos: Position) (context: QueryContext) =
        let s = String.length state.QueryState.Query

        let qs =
            QueryState.setInputMode
            <| InputMode.Select s
            <| QueryState.setCursor state.QueryState s

        state |> InternalState.refresh |> InternalState.updateQueryState qs, pos, context

    let private rotateMatcher (state: InternalState) (pos: Position) (context: QueryContext) =
        let state =
            state
            |> InternalState.rotateMatcher
            |> InternalState.refresh
            |> InternalState.prepareNotification
            |> InternalState.updateWindowWidth

        state, pos, context |> QueryContext.prepareQuery state

    let private rotateOperator (state: InternalState) (pos: Position) (context: QueryContext) =
        let state =
            state
            |> InternalState.rotateOperator
            |> InternalState.refresh
            |> InternalState.updateWindowWidth

        state, pos, context |> QueryContext.prepareQuery state |> QueryContext.prepareTest state

    let private toggleCaseSensitive (state: InternalState) (pos: Position) (context: QueryContext) =
        let state =
            state
            |> InternalState.toggleCaseSensitive
            |> InternalState.refresh
            |> InternalState.updateWindowWidth

        state, pos, context |> QueryContext.prepareQuery state

    let private toggleInvertFilter (state: InternalState) (pos: Position) (context: QueryContext) =
        let state =
            state
            |> InternalState.toggleInvertFilter
            |> InternalState.refresh
            |> InternalState.updateWindowWidth

        state, pos, context |> QueryContext.prepareQuery state

    let private toggleSuppressProperties (state: InternalState) (pos: Position) (context: QueryContext) =
        let state = state |> InternalState.toggleSuppressProperties |> InternalState.refresh

        state, pos, context

    let private (|AlreadyCompleted|_|) (keyword: string) (tail: string) (candidate: string) =
        let rest: string =
            match keyword with
            | "" -> candidate
            | _ -> candidate |> String.replace keyword ""

        let tailHead = String.split " " tail |> Seq.head

        match rest = tailHead with
        | true -> Some(tail.Substring(String.length tailHead))
        | _ -> None

    let private completeProperty (state: InternalState) (pos: Position) (context: QueryContext) =
        let splitQuery keyword candidate =
            let basePosition = state.QueryState.Cursor - String.length keyword
            let head = state.QueryState.Query.Substring(0, basePosition)
            let tail = state.QueryState.Query.Substring(state.QueryState.Cursor)

            match candidate with
            | AlreadyCompleted keyword tail rest -> basePosition, head, rest
            | _ -> basePosition, head, tail

        let buildValues head next tail keyword i candidates basePosition =
            let state =
                { state with
                    InternalState.QueryState.Query = $"%s{head}%s{next}%s{tail}"
                    InternalState.QueryState.Cursor = basePosition + String.length next
                    PropertySearch = PropertySearch.Rotate(keyword, i, candidates) }
                |> InternalState.refresh

            state, pos, context |> QueryContext.prepareQuery state

        match state.PropertySearch with
        | PropertySearch.NoSearch -> InternalState.noRefresh state, pos, context
        | PropertySearch.Search keyword ->
            let candidates =
                state.Properties
                |> Seq.filter (String.lower >> String.startsWith (String.lower keyword))
                |> List.ofSeq

            match candidates |> Seq.length with
            | 0 -> InternalState.noRefresh state, pos, context
            | _ ->
                let candidate = Seq.head candidates
                let basePosition, head, tail = splitQuery keyword candidate
#if DEBUG
                Logger.LogFile [ $"Search keyword '{keyword}' head '{head}' candidate '{candidate}' tail '{tail}'" ]
#endif
                buildValues head candidate tail keyword 0 candidates basePosition
        | PropertySearch.Rotate(keyword, i, candidates) ->
            let cur = candidates |> Seq.item i
            let i = (i + 1) % Seq.length candidates
            let next = candidates |> Seq.item i
            let basePosition, head, tail = splitQuery cur next
#if DEBUG
            Logger.LogFile [ $"Rotate keyword '{keyword}' head '{head}' cur '{cur}' next '{next}' tail '{tail}'" ]
#endif
            buildValues head next tail keyword i candidates basePosition

    let invokeAction (state: InternalState) (pos: Position) (context: QueryContext) (action: Action) =
        match action with
        | Action.Noop -> InternalState.noRefresh state, pos, context
        | Action.AddQuery query -> addQuery state pos context query
        | Action.BackwardChar -> backwardChar state pos context
        | Action.ForwardChar -> forwardChar state pos context
        | Action.BackwardWord -> backwardWord state pos context
        | Action.ForwardWord -> forwardWord state pos context
        | Action.BeginningOfLine -> beginningOfLine state pos context
        | Action.EndOfLine -> endOfLine state pos context
        | Action.DeleteBackwardChar -> deleteBackwardChar state pos context
        | Action.DeleteForwardChar -> deleteForwardChar state pos context
        | Action.DeleteBackwardWord -> deleteBackwardWord state pos context
        | Action.DeleteForwardWord -> deleteForwardWord state pos context
        | Action.DeleteBackwardInput -> deleteBackwardInput state pos context
        | Action.DeleteForwardInput -> deleteForwardInput state pos context
        | Action.SelectBackwardChar -> selectBackwardChar state pos context
        | Action.SelectForwardChar -> selectForwardChar state pos context
        | Action.SelectBackwardWord -> selectBackwardWord state pos context
        | Action.SelectForwardWord -> selectForwardWord state pos context
        | Action.SelectToBeginningOfLine -> selectToBeginningOfLine state pos context
        | Action.SelectToEndOfLine -> selectToEndOfLine state pos context
        | Action.SelectAll -> selectAll state pos context
        | Action.RotateMatcher -> rotateMatcher state pos context
        | Action.RotateOperator -> rotateOperator state pos context
        | Action.ToggleCaseSensitive -> toggleCaseSensitive state pos context
        | Action.ToggleInvertFilter -> toggleInvertFilter state pos context
        | Action.ToggleSuppressProperties -> toggleSuppressProperties state pos context
        | Action.SelectLineUp
        | Action.SelectLineDown
        | Action.ScrollPageUp
        | Action.ScrollPageDown -> InternalState.noRefresh state, pos, context // TODO: implement it.
        | Action.CompleteProperty -> completeProperty state pos context
        | Action.Cancel
        | Action.Finish -> failwithf $"unreachable action received. {action}"
