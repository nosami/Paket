﻿module Paket.RemoteDownload

open Paket
open Newtonsoft.Json.Linq
open System
open System.IO
open Paket.Logging
open Paket.ModuleResolver
open System.IO.Compression
open Paket.Domain

let private githubCache = System.Collections.Concurrent.ConcurrentDictionary<_, _>()
let private lookupDocument (auth,url : string)  = async {
    let key = auth,url
    match githubCache.TryGetValue key with
    | true, document -> return document
    | _ ->
        let! document = safeGetFromUrl(auth, url, null)
        githubCache.TryAdd(key, document) |> ignore
        return document
    }

// Gets the sha1 of a branch
let getSHA1OfBranch origin owner project branch = 
    async { 
        let key = origin,owner,project,branch
        match origin with
        | ModuleResolver.SingleSourceFileOrigin.GitHubLink -> 
            let url = sprintf "https://api.github.com/repos/%s/%s/commits/%s" owner project branch
            let! document = lookupDocument(None,url)
            match document with
            | Some document ->
                let json = JObject.Parse(document)
                return json.["sha"].ToString()
            | None -> 
                failwithf "Could not find hash for %s" url
                return ""
        | ModuleResolver.SingleSourceFileOrigin.GistLink ->
            let url = sprintf "https://api.github.com/gists/%s/%s" project branch
            let! document = lookupDocument(None,url)
            match document with
            | Some document ->
                let json = JObject.Parse(document)
                let latest = json.["history"].First.["version"]
                return latest.ToString()
            | None -> 
                failwithf "Could not find hash for %s" url
                return ""
        | ModuleResolver.SingleSourceFileOrigin.HttpLink _ -> return ""
    }

let private rawFileUrl owner project branch fileName =
    sprintf "https://github.com/%s/%s/raw/%s/%s" owner project branch fileName

let private rawGistFileUrl owner project fileName =
    sprintf "https://gist.githubusercontent.com/%s/%s/raw/%s" owner project fileName

/// Gets a dependencies file from the remote source and tries to parse it.
let downloadDependenciesFile(force,rootPath,groupName,parserF,remoteFile:ModuleResolver.ResolvedSourceFile) = async {
    let fi = FileInfo(remoteFile.Name)

    let dependenciesFileName = remoteFile.Name.Replace(fi.Name,Constants.DependenciesFileName)
    let destination = FileInfo(remoteFile.ComputeFilePath(rootPath,groupName,dependenciesFileName))

    let auth, url = 
        match remoteFile.Origin with
        | ModuleResolver.GitHubLink -> 
            None, rawFileUrl remoteFile.Owner remoteFile.Project remoteFile.Commit dependenciesFileName
        | ModuleResolver.GistLink -> 
            None, rawGistFileUrl remoteFile.Owner remoteFile.Project dependenciesFileName
        | ModuleResolver.HttpLink url -> 
            let url = url.Replace(remoteFile.Name,Constants.DependenciesFileName)
            let auth = 
                ConfigFile.GetCredentialsForUrl remoteFile.Project url
                |> Option.map (fun (un, pwd) -> { Username = un; Password = pwd })
            auth, url

    let exists =
        let di = destination.Directory
        let versionFile = FileInfo(Path.Combine(di.FullName, Constants.PaketVersionFileName))
        not force &&
          not (String.IsNullOrWhiteSpace remoteFile.Commit) && 
          destination.Exists &&
          versionFile.Exists && 
          File.ReadAllText(versionFile.FullName).Contains(remoteFile.Commit)

    
    if exists then
        return parserF (File.ReadAllText(destination.FullName))
    else
        let! result = lookupDocument(auth,url)

        let text,depsFile =
            match result with
            | Some text -> 
                    try
                        text,parserF text
                    with 
                    | _ -> "",parserF ""
            | _ -> "",parserF ""

        Directory.CreateDirectory(destination.FullName |> Path.GetDirectoryName) |> ignore
        File.WriteAllText(destination.FullName, text)

        return depsFile }


let ExtractZip(fileName : string, targetFolder) = 
    Directory.CreateDirectory(targetFolder) |> ignore
    ZipFile.ExtractToDirectory(fileName,targetFolder)

let rec DirectoryCopy(sourceDirName, destDirName, copySubDirs) =
    let dir = new DirectoryInfo(sourceDirName)
    let dirs = dir.GetDirectories()

    if not <| Directory.Exists(destDirName) then
        Directory.CreateDirectory(destDirName) |> ignore

    for file in dir.GetFiles() do
        file.CopyTo(Path.Combine(destDirName, file.Name), true) |> ignore

    // If copying subdirectories, copy them and their contents to new location. 
    if copySubDirs then
        for subdir in dirs do
            DirectoryCopy(subdir.FullName, Path.Combine(destDirName, subdir.Name), copySubDirs)

/// Gets a single file from github.
let downloadRemoteFiles(remoteFile:ResolvedSourceFile,destination) = async {
    match remoteFile.Origin, remoteFile.Name with
    | SingleSourceFileOrigin.GistLink, Constants.FullProjectSourceFileName ->
        let fi = FileInfo(destination)
        let projectPath = fi.Directory.FullName

        let url = sprintf "https://api.github.com/gists/%s/%s" remoteFile.Project remoteFile.Commit
        let! document = getFromUrl(None, url, null)
        let json = JObject.Parse(document)
        let files = json.["files"] |> Seq.map (fun i -> i.First.["filename"].ToString(), i.First.["raw_url"].ToString())

        let task = 
            files |> Seq.map (fun (filename, url) -> 
                async {
                    let path = Path.Combine(projectPath,filename)
                    do! downloadFromUrl(None, url) path
                } 
            ) |> Async.Parallel
        task |> Async.RunSynchronously |> ignore

        // GIST currently does not support zip-packages, so now this fetches all files separately.
        // let downloadUrl = sprintf "https://gist.github.com/%s/%s/download" remoteFile.Owner remoteFile.Project //is a tar.gz

    | SingleSourceFileOrigin.GitHubLink, Constants.FullProjectSourceFileName -> 
        let fi = FileInfo(destination)
        let projectPath = fi.Directory.FullName
        let zipFile = Path.Combine(projectPath,sprintf "%s.zip" remoteFile.Commit)
        let downloadUrl = sprintf "https://github.com/%s/%s/archive/%s.zip" remoteFile.Owner remoteFile.Project remoteFile.Commit

        do! downloadFromUrl(None, downloadUrl) zipFile

        ExtractZip(zipFile,projectPath)

        let source = Path.Combine(projectPath, sprintf "%s-%s" remoteFile.Project remoteFile.Commit)
        DirectoryCopy(source,projectPath,true)
    | SingleSourceFileOrigin.GistLink, _ -> 
        return! downloadFromUrl(None,rawGistFileUrl remoteFile.Owner remoteFile.Project remoteFile.Name) destination
    | SingleSourceFileOrigin.GitHubLink, _ ->
        return! downloadFromUrl(None,rawFileUrl remoteFile.Owner remoteFile.Project remoteFile.Commit remoteFile.Name) destination
    | SingleSourceFileOrigin.HttpLink(origin), _ ->
        let url = origin + remoteFile.Commit
        let auth =
            ConfigFile.GetCredentialsForUrl remoteFile.Project url
            |> Option.map (fun (un, pwd) -> { Username = un; Password = pwd })
        do! downloadFromUrl(auth, url) destination
        match Path.GetExtension(destination).ToLowerInvariant() with
        | ".zip" ->
            let targetFolder = FileInfo(destination).Directory.FullName
            ExtractZip(destination, targetFolder)
        | _ -> ignore()
}

let DownloadSourceFiles(rootPath, groupName, force, sourceFiles:ModuleResolver.ResolvedSourceFile list) =
    sourceFiles
    |> List.map (fun source ->
        let destination = source.FilePath(rootPath,groupName)
        let destinationDir = FileInfo(destination).Directory.FullName

        (destinationDir, source.Commit), (destination, source))
    |> List.groupBy fst
    |> List.sortBy (fst >> fst)
    |> List.map (fun ((destinationDir, version), sources) ->
        let versionFile = FileInfo(Path.Combine(destinationDir, Constants.PaketVersionFileName))
        let isInRightVersion = versionFile.Exists && File.ReadAllText(versionFile.FullName).Contains(version)

        if not isInRightVersion then
            CleanDir destinationDir

        (versionFile, version), sources)
    |> List.map (fun ((versionFile, version), sources) ->
        async {
            let! downloaded =
                sources
                |> List.map (fun (_, (destination, source)) ->
                    async {
                        let exists =
                            if destination.EndsWith Constants.FullProjectSourceFileName then
                                let di = FileInfo(destination).Directory
                                di.Exists && FileInfo(Path.Combine(di.FullName, Constants.PaketVersionFileName)).Exists
                            else
                                File.Exists destination

                        if not force && exists then
                            verbosefn "Sourcefile %O is already there." source
                        else 
                            tracefn "Downloading %O to %s" source destination
                            do! downloadRemoteFiles(source,destination)
                    })
                |> Async.Parallel

            if File.Exists(versionFile.FullName) then
                if not <| File.ReadAllText(versionFile.FullName).Contains(version) then
                    File.AppendAllLines(versionFile.FullName, [version])
            else
                File.AppendAllLines(versionFile.FullName, [version])
        })
    |> Async.Parallel