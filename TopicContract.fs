module TopicContract

type MessageId = int

type 'content Message = {
    Content : 'content
    Id : MessageId
}

type ConsumerRequest =
    | Repeat
    | Next

type 'content Reply =
    | Ready of 'content Message
    | Pending
    | Shutdown

type 'content ITopicApi = {
    Add : 'content -> MessageId Async
    TryGet : ConsumerRequest -> 'content Reply Async
    Shutdown : unit -> unit Async
}