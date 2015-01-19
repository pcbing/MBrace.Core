﻿namespace MBrace.SampleRuntime

open System.IO
open System.Diagnostics
open System.Threading

open Nessos.Thespian
open Nessos.Thespian.Remote
open MBrace
open MBrace.Store
open MBrace.Continuation
open MBrace.Runtime
open MBrace.Runtime.Vagrant
open MBrace.Runtime.Compiler
open MBrace.SampleRuntime.Tasks
open MBrace.SampleRuntime.RuntimeProvider

#nowarn "40"

/// BASE64 serialized argument parsing schema
module internal Argument =
    let ofRuntime (runtime : RuntimeState) =
        let pickle = VagrantRegistry.Pickler.Pickle(runtime)
        System.Convert.ToBase64String pickle

    let toRuntime (args : string []) =
        let bytes = System.Convert.FromBase64String(args.[0])
        VagrantRegistry.Pickler.UnPickle<RuntimeState> bytes

/// MBrace Sample runtime client instance.
type MBraceRuntime private (logger : string -> unit) =
    static let mutable exe = None
    static let initWorkers (target : RuntimeState) (count : int) =
        if count < 1 then invalidArg "workerCount" "must be positive."
        let exe = MBraceRuntime.WorkerExecutable    
        let args = Argument.ofRuntime target
        let psi = new ProcessStartInfo(exe, args)
        psi.WorkingDirectory <- Path.GetDirectoryName exe
        psi.UseShellExecute <- true
        Array.init count (fun _ -> Process.Start psi)

    let mutable procs = [||]
    let mutable workerManagers = Array.empty
    let getWorkerRefs () =
        if procs.Length > 0 then procs |> Array.map (fun (p: Process) -> new Worker(p.Id.ToString()) :> IWorkerRef)
        else workerManagers |> Array.map (fun p -> new Worker(p) :> IWorkerRef)
    let state = RuntimeState.InitLocal logger getWorkerRefs
    let atomProvider = new ActorAtomProvider(state) :> ICloudAtomProvider
    let channelProvider = new ActorChannelProvider(state) :> ICloudChannelProvider

    let appendWorker (address: string) =
        let url = sprintf "utcp://%s/workerManager" address
        let workerManager = ActorRef.fromUri url
        let state = Argument.ofRuntime state
        workerManager <!= fun ch -> Actors.WorkerManager.SubscribeToRuntime(ch, state, 10)
        workerManager.Id.ToString()

    let createProcessInfo () =
        {
            ProcessId = System.Guid.NewGuid().ToString()
            DefaultDirectory = Config.getFileStore().CreateUniqueDirectoryPath()
            DefaultAtomContainer = atomProvider.CreateUniqueContainerName()
            DefaultChannelContainer = channelProvider.CreateUniqueContainerName()
        }
        

    /// <summary>
    ///     Asynchronously execute a workflow on the distributed runtime.
    /// </summary>
    /// <param name="workflow">Workflow to be executed.</param>
    /// <param name="cancellationToken">Cancellation token for computation.</param>
    /// <param name="faultPolicy">Fault policy. Defaults to infinite retries.</param>
    member __.RunAsync(workflow : Cloud<'T>, ?cancellationToken : CancellationToken, ?faultPolicy) = async {
        let faultPolicy = match faultPolicy with Some fp -> fp | None -> FaultPolicy.InfiniteRetry()
        let computation = CloudCompiler.Compile workflow
        let processInfo = createProcessInfo ()

        let! cts = state.ResourceFactory.RequestCancellationTokenSource()
        try
            cancellationToken |> Option.iter (fun ct -> ct.Register(fun () -> cts.Cancel()) |> ignore)
            let! resultCell = state.StartAsCell processInfo computation.Dependencies cts faultPolicy None computation.Workflow
            let! result = resultCell.AwaitResult()
            return result.Value
        finally
            cts.Cancel ()
    }

    /// <summary>
    ///     Execute a workflow on the distributed runtime as task.
    /// </summary>
    /// <param name="workflow">Workflow to be executed.</param>
    /// <param name="cancellationToken">Cancellation token for computation.</param>
    /// <param name="faultPolicy">Fault policy. Defaults to infinite retries.</param>
    member __.RunAsTask(workflow : Cloud<'T>, ?cancellationToken : CancellationToken, ?faultPolicy) =
        let asyncwf = __.RunAsync(workflow, ?cancellationToken = cancellationToken, ?faultPolicy = faultPolicy)
        Async.StartAsTask(asyncwf)

    /// <summary>
    ///     Execute a workflow on the distributed runtime synchronously
    /// </summary>
    /// <param name="workflow">Workflow to be executed.</param>
    /// <param name="cancellationToken">Cancellation token for computation.</param>
    /// <param name="faultPolicy">Fault policy. Defaults to infinite retries.</param>
    member __.Run(workflow : Cloud<'T>, ?cancellationToken : CancellationToken, ?faultPolicy) =
        __.RunAsync(workflow, ?cancellationToken = cancellationToken, ?faultPolicy = faultPolicy) |> Async.RunSync

    /// <summary>
    ///     Run workflow as local, in-memory computation
    /// </summary>
    /// <param name="workflow">Workflow to execute</param>
    member __.RunLocalAsync(workflow : Cloud<'T>) : Async<'T> =
        let procInfo = createProcessInfo ()
        let runtimeP = RuntimeProvider.RuntimeProvider.CreateInMemoryRuntime(state, procInfo)
        let resources = resource {
            yield Config.getFileStoreConfiguration procInfo.DefaultDirectory
            yield atomProvider
            yield channelProvider
            yield runtimeP :> ICloudRuntimeProvider
        }

        Cloud.ToAsync(workflow, resources = resources)

    /// <summary>
    ///     Run workflow as local, in-memory computation
    /// </summary>
    /// <param name="workflow">Workflow to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    member __.RunLocal(workflow, ?cancellationToken) : 'T = 
        let wf = __.RunLocalAsync(workflow) in Async.RunSync(wf, ?cancellationToken = cancellationToken)

    /// Violently kills all worker nodes in the runtime
    member __.KillAllWorkers () = lock procs (fun () -> for p in procs do try p.Kill() with _ -> () ; procs <- [||])
    /// Gets all worker processes in the runtime
    member __.Workers = procs
    /// Appens count of new worker processes to the runtime.
    member __.AppendWorkers (count : int) =
        let newProcs = initWorkers state count
        lock procs (fun () -> procs <- Array.append procs newProcs)

    member __.AppendWorkers (addresses: string[]) =
        lock workerManagers (fun () -> workerManagers <- addresses |> Array.map appendWorker)

    static member Init(workers: string[], ?logger: string -> unit) =
        let logger = defaultArg logger ignore
        let client = new MBraceRuntime(logger)
        client.AppendWorkers workers
        client

    /// Initialize a new local rutime instance with supplied worker count.
    static member InitLocal(workerCount : int, ?logger : string -> unit) =
        let logger = defaultArg logger ignore
        let client = new MBraceRuntime(logger)
        client.AppendWorkers(workerCount)
        client

    /// Gets or sets the worker executable location.
    static member WorkerExecutable
        with get () = match exe with None -> invalidOp "unset executable path." | Some e -> e
        and set path = 
            let path = Path.GetFullPath path
            if File.Exists path then exe <- Some path
            else raise <| FileNotFoundException(path)