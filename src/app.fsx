#if INTERACTIVE
#r "System.Xml.Linq.dll"
#r "../packages/Suave/lib/net40/Suave.dll"
#r "../packages/Newtonsoft.Json/lib/net40/Newtonsoft.Json.dll"
#load "../packages/FSharp.Azure.StorageTypeProvider/StorageTypeProvider.fsx"
#load "config.fs"
#else
module Logging
#endif
open Suave
open System
open System.IO
open FSharp.Data
open Suave.Filters
open Suave.Writers
open Suave.Operators
open System.Collections.Generic
open Microsoft.WindowsAzure.Storage
open Newtonsoft.Json

// --------------------------------------------------------------------------------------
// Data we store about snippets
// --------------------------------------------------------------------------------------

type Snippet = 
  { id : int
    likes : int
    posted : DateTime
    title : string
    description : string
    author : string
    twitter : string
    code : string 
    compiled : string 
    version : string
    config : string
    hidden : bool }

type NewSnippet = 
  { title : string
    description : string
    author : string
    twitter : string
    compiled : string
    code : string 
    hidden : bool
    config : string
    version : string }

// --------------------------------------------------------------------------------------
// Reading & writing blobs in Azure storage
// --------------------------------------------------------------------------------------

#if INTERACTIVE
let createCloudBlobClient() = 
  let account = CloudStorageAccount.Parse(Config.TheGammaSnippetsStorage)
  account.CreateCloudBlobClient()
#else
let createCloudBlobClient() = 
  let account = CloudStorageAccount.Parse(Environment.GetEnvironmentVariable("CUSTOMCONNSTR_THEGAMMASNIPS_STORAGE"))
  account.CreateCloudBlobClient()
#endif

let serializer = JsonSerializer.Create()

let toJson value = 
  let sb = System.Text.StringBuilder()
  use tw = new System.IO.StringWriter(sb)
  serializer.Serialize(tw, value)
  sb.ToString() 

let fromJson<'R> str : 'R = 
  use tr = new System.IO.StringReader(str)
  serializer.Deserialize(tr, typeof<'R>) :?> 'R

let readSnippets source =
  let container = createCloudBlobClient().GetContainerReference(source)
  if container.Exists() then
    let blob = container.GetBlockBlobReference("snippets.json")
    if blob.Exists() then 
      let json = blob.DownloadText(System.Text.Encoding.UTF8) 
      json, json |> fromJson<Snippet[]> 
    else failwith "Blob 'snippets.json' does not exist."
  else failwithf "Container '%s' not found" source

let writeSnippets source (snippets:Snippet[]) = 
  let json = snippets |> toJson
  let container = createCloudBlobClient().GetContainerReference(source)
  if container.Exists() then
    let blob = container.GetBlockBlobReference("snippets.json")
    blob.UploadText(json, System.Text.Encoding.UTF8)
  else failwithf "container '%s' not found" source

// --------------------------------------------------------------------------------------
// Keeping current snippets using an agent
// --------------------------------------------------------------------------------------

type Message = 
  | GetSnippets of string * AsyncReplyChannel<string>
  | AddSnippet of string * NewSnippet * AsyncReplyChannel<int>
  | LikeSnippet of string * int

let snippets = MailboxProcessor.Start(fun inbox -> 
  let rec loop snippets = async {
    let! msg = inbox.Receive()
    match msg with
    | GetSnippets(source, res) ->
        match Map.tryFind source snippets with
        | Some(json, _) -> 
            res.Reply(json)
            return! loop snippets
        | None ->
            let json, snips = readSnippets source
            res.Reply(json)
            return! loop (Map.add source (json, snips) snippets)
    | AddSnippet(source, snip, res) ->
        let _, snips = snippets.[source]
        let id = 1 + (snips |> Seq.map (fun s -> s.id) |> Seq.fold max 0)
        let snippet = 
          { id = id; likes = 0; posted = DateTime.Now; author = snip.author
            version = snip.version; hidden = snip.hidden; title = snip.title 
            compiled = snip.compiled; code = snip.code; config = snip.config 
            description = snip.description; twitter = snip.twitter }
        let snips = Array.append snips [| snippet |]
        writeSnippets source snips
        res.Reply(id)
        return! loop (Map.add source (toJson snips, snips) snippets)
    | LikeSnippet(source, id) ->
        let snips = snippets.[source] |> snd |> Array.map (fun s -> 
            if s.id = id then { s with likes = s.likes + 1 } else s)
        writeSnippets source snips
        return! loop (Map.add source (toJson snips, snips) snippets)  }
  async { 
    while true do
      try return! loop Map.empty
      with e -> printfn "Agent failed: %A" e })

// --------------------------------------------------------------------------------------
// REST API for Snippets
// --------------------------------------------------------------------------------------

let likeSnippet = 
  GET >=> pathScan "/%s/%d/like" (fun (source, id) ->
    snippets.Post(LikeSnippet (source, id))
    Successful.OK "Liked")

let postSnippet = 
  POST >=> pathScan "/%s" (fun source ctx -> async {
    let snip = Text.Encoding.UTF8.GetString(ctx.request.rawForm) |> fromJson<NewSnippet>
    let! id = snippets.PostAndAsyncReply(fun ch -> AddSnippet(source, snip, ch))
    return! Successful.CREATED (string id) ctx })

let getSnippets = 
  GET >=> pathScan "/%s" (fun source ctx -> async {
    let! json = snippets.PostAndAsyncReply(fun ch -> GetSnippets(source, ch))
    return! Successful.OK json ctx })

let app =
  setHeader  "Access-Control-Allow-Origin" "*"
  >=> setHeader "Access-Control-Allow-Headers" "content-type"
  >=> choose [
    OPTIONS >=> Successful.OK "CORS approved"
    GET >=> path "/" >=> Successful.OK "Service is running..."
    getSnippets
    postSnippet
    likeSnippet ]


// When port was specified, we start the app (in Azure), 
// otherwise we do nothing (it is hosted by 'build.fsx')
match System.Environment.GetCommandLineArgs() |> Seq.tryPick (fun s ->
    if s.StartsWith("port=") then Some(int(s.Substring("port=".Length)))
    else None ) with
| Some port ->
    let serverConfig =
      { Web.defaultConfig with
          logger = Logging.Loggers.saneDefaultsFor Logging.LogLevel.Warn
          bindings = [ HttpBinding.mkSimple HTTP "127.0.0.1" port ] }
    Web.startWebServer serverConfig app
| _ -> ()