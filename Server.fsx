#i "nuget: https://api.nuget.org/v3/index.json"

#r "nuget: Fable.Remoting.Suave, 4.36.0"
#r "nuget: Hopac, 0.5.0"
#r "nuget: FSharpx.Collections, 3.1.0"

#load "TopicContract.fs"

open Suave
open Fable.Remoting.Server
open Fable.Remoting.Suave
open Hopac
open TopicContract
open Hopac.Infixes
open FSharpx.Collections

let inline (^) f x = f x

let createApi<'a> () : 'a ITopicApi = 
    let shutdown = IVar()

    let producers = Mailbox()

    let consumers = Mailbox()

    let tryFindPendedConsumer, trySubmitPendedConsumer =
        let pended = Ch ()
        Ch.take pended
        , fun request reply -> 
            Ch.give pended (request, reply)

    let pendConsumer request reply = Job.start ^ Alt.choose [
        shutdown ^=>. reply Reply.Shutdown
        timeOutMillis 60_000 ^=>. reply Reply.Pending
        trySubmitPendedConsumer request reply
    ]

    let fixedPendedConsumers = Mailbox()

    let tryFixPendedConsumer = Alt.choose [
        tryFindPendedConsumer ^=> Mailbox.send fixedPendedConsumers 

        Alt.unit()
    ]

    let waitNext current lastId queue = Alt.choose [
        let proceed = current, lastId, queue
        
        fixedPendedConsumers ^=> fun (request, reply) -> job {
            match request, queue with 
            | Next, Deque.Cons (next, queue) ->
                do! asJob ^ reply ^ Reply.Ready next
                return next, lastId, queue
            | _ ->
                do! asJob ^ reply ^ Reply.Ready current
                return proceed
        }
        shutdown ^=>. Job.abort ()
        producers ^=> fun (content, reply) -> job {
            let lastId = lastId + 1
            let last = {
                Message.Id = lastId
                Content = content
            }
            do! asJob ^ reply last.Id
            do! tryFixPendedConsumer
            return current, lastId, queue.Conj last
        } 
        consumers ^=> fun (request, reply) -> job {
            match request, queue with 
            | Repeat, _ ->
                do! asJob ^ reply ^ Reply.Ready current
                return proceed
            | Next, Deque.Cons (next, queue) ->
                do! asJob ^ reply ^ Reply.Ready next
                return next, lastId, queue
            | Next, _ ->
                do! pendConsumer request reply
                return proceed
        }
    ]

    let waitFirst = Alt.choose [
        shutdown ^=>. Job.abort ()
        producers ^=> fun (content, reply) -> job {
            let current = {
                Message.Id = 1
                Content = content
            }
            do! asJob ^ reply current.Id
            do! tryFixPendedConsumer
            do! Job.iterateServer (current, current.Id, Deque.empty) ^ fun (current, lastId, queue) -> 
                    waitNext current lastId queue
                |> Job.start
            return! Job.abort()
        }
        consumers ^=> fun (request, reply) -> job {
            match request with
            | Repeat -> do! pendConsumer request reply
            | Next -> do! asJob ^ reply Reply.Shutdown
        }
    ]

    Job.foreverServer waitFirst
    |> start

    let exchangeWith mailbox input = Job.toAsync ^ job {
        let reply = IVar()
        do! Mailbox.send mailbox (input, IVar.tryFill reply)
        return! reply
    }

    {
        Shutdown = fun () -> 
            IVar.tryFill shutdown ()
            |> Job.toAsync 
        Add = exchangeWith producers
        TryGet = exchangeWith consumers
    }

let webApp = choose [
    Remoting.createApi()
    |> Remoting.fromValue (createApi<string>())
    |> Remoting.buildWebPart
]

let cts = new System.Threading.CancellationTokenSource()

startWebServerAsync { defaultConfig with cancellationToken = cts.Token } webApp
||> fun startedData wait ->
    async {
        let! startedData = startedData
        for item in Seq.catOptions startedData do
            printfn "%A" item.binding.endpoint
    }
    |> Async.Start
    Async.Start(
        wait
        , cts.Token
    )

cts.Cancel()