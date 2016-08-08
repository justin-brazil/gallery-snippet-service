#if INTERACTIVE
#r "System.Xml.Linq.dll"
#r "../packages/Suave/lib/net40/Suave.dll"
#r "../packages/Newtonsoft.Json/lib/net40/Newtonsoft.Json.dll"
#load "../packages/FSharp.Azure.StorageTypeProvider/StorageTypeProvider.fsx"
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
    code : string }

// --------------------------------------------------------------------------------------
// Reading & writing blobs in Azure storage
// --------------------------------------------------------------------------------------

let createCloudBlobClient() = 
  let account = CloudStorageAccount.Parse(Environment.GetEnvironmentVariable("CUSTOMCONNSTR_THEGAMMASNIPS_STORAGE"))
  account.CreateCloudBlobClient()

let serializer = JsonSerializer.Create()

let toJson value = 
  let sb = System.Text.StringBuilder()
  use tw = new System.IO.StringWriter(sb)
  serializer.Serialize(tw, value)
  sb.ToString() 

let fromJson<'R> str : 'R = 
  use tr = new System.IO.StringReader(str)
  serializer.Deserialize(tr, typeof<'R>) :?> 'R

let readSnippets () =
  let container = createCloudBlobClient().GetContainerReference("olympics")
  if container.Exists() then
    let blob = container.GetBlockBlobReference("snippets.json")
    if blob.Exists() then 
      let json = blob.DownloadText(System.Text.Encoding.UTF8) 
      json, json |> fromJson<Snippet[]> |> List.ofArray
    else failwith "Blob 'snippets.json' does not exist."
  else failwith "container 'olympics' not found" 

let writeSnippets (json:string) = 
  let container = createCloudBlobClient().GetContainerReference("olympics")
  if container.Exists() then
    let blob = container.GetBlockBlobReference("snippets.json")
    blob.UploadText(json, System.Text.Encoding.UTF8)
  else failwith "container 'olympics' not found" 


// --------------------------------------------------------------------------------------
// Keeping current snippets using an agent
// --------------------------------------------------------------------------------------

type Message = 
  | GetSnippets of AsyncReplyChannel<string>
  | AddSnippet of Snippet * AsyncReplyChannel<int>
  | LikeSnippet of int

let snippets = MailboxProcessor.Start(fun inbox -> 
  let rec loop (json, snippets) = async {
    let! msg = inbox.Receive()
    match msg with
    | GetSnippets(res) ->
        res.Reply(json)
        return! loop (json, snippets)
    | AddSnippet(snip, res) ->
        let id = 1 + (snippets |> Seq.map (fun s -> s.id) |> Seq.max)
        let snippets = { snip with id = id }::snippets
        let json = snippets |> Array.ofList |> toJson
        res.Reply(id)
        writeSnippets json
        return! loop (json, snippets)
    | LikeSnippet(id) ->
        let snippets = snippets |> List.map (fun s -> if s.id = id then { s with likes = s.likes + 1 } else s)
        let json = snippets |> Array.ofList |> toJson
        writeSnippets json
        return! loop (json, snippets) }
  loop (readSnippets()) )

// --------------------------------------------------------------------------------------
// REST API for Snippets
// --------------------------------------------------------------------------------------

let likeSnippet = 
  GET >=> pathScan "/olympics/%d/like" (fun id ->
    snippets.Post(LikeSnippet id)
    Successful.OK "Liked")

let postSnippet = 
  POST >=> path "/olympics" >=> request (fun req ctx -> async {
    let snip = Text.Encoding.UTF8.GetString(req.rawForm) |> fromJson<Snippet>
    let! id = snippets.PostAndAsyncReply(fun ch -> AddSnippet(snip, ch))
    return! Successful.CREATED (string id) ctx })

let getSnippets = 
  GET >=> path "/olympics" >=> fun ctx -> async {
    let! json = snippets.PostAndAsyncReply(GetSnippets)
    return! Successful.OK json ctx }

let app =
  setHeader  "Access-Control-Allow-Origin" "*"
  >=> setHeader "Access-Control-Allow-Headers" "content-type"
  >=> choose [
    OPTIONS >=> Successful.OK "CORS approved"
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