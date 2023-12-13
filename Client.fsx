#i "nuget: https://api.nuget.org/v3/index.json"

#r "nuget: Fable.Remoting.DotnetClient, 3.31.0"
#r "nuget: Hopac, 0.5.0"

#load "TopicContract.fs"

open Fable.Remoting.DotnetClient
open TopicContract
open Hopac
open Hopac.Infixes

let inline (^) f x = f x

let client =
    Remoting.createApi "http://127.0.0.1:8080"
    |> Remoting.buildProxy<ITopicApi<string>>

client.Add "Hello"
|> Async.RunSynchronously

client.Add "world!"
|> Async.RunSynchronously

async {
    let! response = client.TryGet ConsumerRequest.Repeat
    printfn "%A" response
}
|> Async.Start

async {
    let! response = client.TryGet ConsumerRequest.Next
    printfn "%A" response
}
|> Async.Start

let iterateTopicViaServer (action : _ -> _ Job) =
    let onShutdown = Job.abort()
    Job.foreverServer ^ job {
        match! client.TryGet ConsumerRequest.Repeat with
        | Reply.Pending -> () 
        | Reply.Ready current ->
            do! action current
            do! Job.start ^ Job.foreverServer ^ job { 
                match! client.TryGet ConsumerRequest.Next with
                | Reply.Pending -> ()
                | Reply.Ready msg -> do! action msg
                | Reply.Shutdown -> do! onShutdown
            }
            do! Job.abort()
        | Reply.Shutdown -> do! onShutdown
    }
    // |> Job.start

let iterateTopicViaStreams action =
    job {
        return! client.TryGet ConsumerRequest.Repeat
    }
    |> Stream.indefinitely
    |> Stream.tryPickFun ^ function
        | Reply.Pending -> None
        | reply -> Some reply
    |> Stream.once
    |> Stream.choose
    |> Stream.append <| (
        job {
            return! client.TryGet ConsumerRequest.Next
        }
        |> Stream.indefinitely 
    )
    |> Stream.takeWhileFun ^ function
        | Reply.Shutdown -> false
        | _ -> true
    |> Stream.chooseFun ^ function
        | Reply.Ready msg -> Some msg
        | _ -> None
    |> Stream.consumeJob ^ fun msg -> action msg