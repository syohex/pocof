namespace pocof

open System
open System.Management.Automation
open System.Management.Automation.Host
open System.Threading
open System.Management.Automation.Runspaces
open System.Collections

[<Cmdlet(VerbsCommon.Select, "Pocof")>]
[<Alias("pocof")>]
[<OutputType(typeof<PSObject>)>]
type SelectPocofCommand() =
    inherit PSCmdlet()

    let mutable input: obj list = []

    let mutable caseSensitive: bool = false
    let mutable invertQuery: bool = false
    let mutable nonInteractive: bool = false

    let interact
        (conf: PocofData.InternalConfig)
        (state: PocofData.InternalState)
        (pos: PocofData.Position)
        (rui: PSHostRawUserInterface)
        (invoke: list<obj> -> seq<string>)
        =

        if conf.NotInteractive then
            let _, l = PocofQuery.run state input
            l
        else
            use sbf = PocofScreen.init rui conf.Prompt invoke

            let writeScreen =
                match conf.Layout with
                | PocofData.TopDown -> sbf.writeTopDown
                | PocofData.BottomUp -> sbf.writeBottomUp

            let rec loop (state: PocofData.InternalState) (pos: PocofData.Position) (results: obj list) (skip: bool) =
                let s, l =
                    if skip then
                        state, results
                    else
                        let s, l = PocofQuery.run state input
                        writeScreen s pos.X l
                        s, l

                if Console.KeyAvailable then
                    match PocofAction.get conf.Keymaps (fun () -> Console.ReadKey true) with
                    | PocofData.Cancel -> []
                    | PocofData.Finish -> l
                    | a ->
                        PocofData.invokeAction a s pos
                        |> fun (s, p) -> loop s p l false
                else
                    Thread.Sleep(50) // NOTE: to avoid high load.
                    loop s pos l true

            loop state pos input false

    [<Parameter(Position = 0, ValueFromPipeline = true, ValueFromPipelineByPropertyName = true)>]
    member val InputObject: PSObject [] = [||] with get, set

    [<Parameter>]
    member val Query = String.Empty with get, set

    [<Parameter>]
    [<ValidateSet("match", "like", "eq")>]
    member val Matcher = PocofData.MATCH.ToString().ToLower() with get, set

    [<Parameter>]
    member __.CaseSensitive: SwitchParameter = new SwitchParameter(false)

    member __.CaseSensitive
        with set (v: SwitchParameter) = caseSensitive <- v.IsPresent

    [<Parameter>]
    member __.InvertQuery: SwitchParameter = new SwitchParameter(false)

    member __.InvertQuery
        with set (v: SwitchParameter) = invertQuery <- v.IsPresent

    [<Parameter>]
    member val Prompt = "query" with get, set

    [<Parameter>]
    [<ValidateSet("TopDown"
      // , "BottomUp"
      )>]
    member val Layout = PocofData.TopDown.ToString() with get, set

    [<Parameter>]
    member val Keymaps: Hashtable = null with get, set

    [<Parameter>]
    member __.NonInteractive: SwitchParameter = new SwitchParameter(false)

    member __.NonInteractive
        with set (v: SwitchParameter) = nonInteractive <- v.IsPresent

    member __.invoke inp =
        __.InvokeCommand.InvokeScript(
            @"$input | Format-Table | Out-String",
            true,
            PipelineResultTypes.None,
            Array.ofList (inp),
            null
        )
        |> Seq.map (fun o -> o.ToString())

    override __.BeginProcessing() = base.BeginProcessing()

    override __.ProcessRecord() =
        input <-
            List.ofArray __.InputObject
            |> List.fold
                (fun acc o ->
                    match o.BaseObject with
                    | :? IDictionary as dct ->
                        Seq.cast<obj> dct
                        |> Seq.fold (fun a d -> d :: a) acc
                    | _ as o -> o :: acc)
                input

    override __.EndProcessing() =
        input <- List.rev input

        let conf, state, pos =
            PocofData.initConfig
                { Query = __.Query
                  Matcher = __.Matcher
                  CaseSensitive = caseSensitive
                  InvertQuery = invertQuery
                  Prompt = __.Prompt
                  Layout = __.Layout
                  Keymaps = __.Keymaps
                  NotInteractive = nonInteractive }

        interact conf state pos __.Host.UI.RawUI __.invoke
        |> Seq.iter __.WriteObject
