﻿// Copyright (c) 2019 - for information on the respective copyright owner
// see the NOTICE file and/or the repository 
// https://github.com/blech-lang/blech.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

//=============================================================================
// This module declares types and functions for
// representing paths to Blech compilation units (.blc, blh, package)
// A translation unit path consists of 
//    - a package name, 
//    - a directory path (within this package)
//    - a file name (without extension suffix)
// The functions defined here create and search for such paths.
//=============================================================================

module Blech.Common.TranslationUnitPath


open System // for String
open System.IO // for Path
open PPrint

/// Constants =================================================================

let dot = '.'     // module name separator
let slash = '/'   // directory separator in includes and import paths
let underscore = '_' // directory separator in include guards

let glob = '?'
let dirSep = ';'  // directory separator for searchpaths
let box = "box:"

let implementationFileExtension = ".blc"
let interfaceFileExtension = ".blh"
let hFileExtension = ".h"
let cFileExtension = ".c"
let hGuardExtension = "_H_INCLUDED"
let blech = "blech"  // reserved name and keyword for code generation purposes


/// Local Submodule ===========================================================
[<RequireQualifiedAccess>]
module PathRegex =
    open FParsec

    // Names for capturing groups
    type Navigation =
        | Up of int
        | Pack of string
        | Here
        | Root
    
    // `identifier` taken from https://www.quanttec.com/fparsec/tutorial.html#parsing-string-data
    let private identifier =
        let isIdentifierFirstChar c = isLetter c || c = '_'
        let isIdentifierChar c = isLetter c || isDigit c || c = '_'
    
        many1Satisfy2L // _L version of the parser additionally expects an error "label"
            isIdentifierFirstChar 
            isIdentifierChar 
            "expected an identifier starting with a letter or underscore"
        .>> spaces // skips trailing whitespace
    
    let private slash = pstring "/" // prstring is a predefined parser, that parses a given string
    
    let private thisDir =
        pstring "./" >>% Here // >>% if the parser on the left succeeds, return the value on the right as the result
    
    let private parentDir =
        pstring "../" >>% ()
    
    let private dirUp =
        many1 parentDir // many1 succeeds if the given parser succeeds one or more times
        |>> (List.length >> Up) // |>> takes the result of the parser on the left and applies the function on the right 
    
    let private package =
        pstring box >>. identifier .>> slash // >>. returns the result of the right parser
        |>> Pack                               // .>> returns the result of the left parser
    
    let private root =
        slash
        >>% Root
    
    let private navigationPrefix =
        choice [package; root; dirUp; thisDir]
        |> opt
        |>> (function Some x -> x | None -> Here) // this makes ./ optional
    
    let private directories =
        identifier .>>? slash // ? means backtrack if not successful
        |> many
    
    let private path =
        directories .>>. identifier // .>>. returns left * right result

    // import paths have the following form
    // box:package/a/b/file  - external package import
    // /a/b/file            - absolute in-package import with '/' under package dir
    // b/file               - relative in-package import from current directory
    // ./b/file             - relative in-package import with ./ indicating current directory
    // ../../b/file         - relative in-package import with ../ up from current directory
    let parseImportPath input =
        match run (navigationPrefix .>>. path .>> eof) input with
        | ParserResult.Success (res,_,_) -> 
            Result.Ok res
        | ParserResult.Failure (msg,perror,_) -> 
            // see https://www.quanttec.com/fparsec/reference/error.html
            let rec mapToStr (msgs: ErrorMessageList) =
                ErrorMessageList.ToSortedArray(msgs)
                |> List.ofArray
                |> List.map (
                    function
                    | Expected msg -> msg
                    | ExpectedString msg -> msg
                    | ExpectedStringCI msg -> msg
                    | Unexpected msg -> msg
                    | UnexpectedString msg -> msg
                    | UnexpectedStringCI msg -> msg
                    | Message msg -> msg
                    | NestedError (_,_, msgs)
                    | CompoundError (_,_,_,msgs) ->
                        // recurse
                        mapToStr msgs
                    | OtherErrorMessage _ ->
                        failwith "Unknown error in import path parser."
                    | _ ->
                        failwith "printMessages"
                    )
                |> String.concat ", "
            (mapToStr perror.Messages, int perror.Position.Column)
            |> Result.Error 
            //perror.Messages 
            //|> ErrorMessageList.ToSortedArray 
            //|> List.ofArray 
            //|> Result.Error 

    open System.Text.RegularExpressions
    [<Literal>]
    let ReservedId = "blech"  // reserved Blech identifier, not a keyword
    [<Literal>]
    let Id = "^_*[a-zA-Z]+[_a-zA-Z0-9]*$"  // a valid Blech identifier, cannot not start with a digit or a digit after '_'

    /// Checks if a directory or file name - without extension - can be used as a Blech identifier
    // also used by the language server
    let isValidFileOrDirectoryName name =
        let isId = (Regex Id).IsMatch
        not (ReservedId.Equals name) && isId name
// end of PathRegex module


/// Types =====================================================================

type TranslationUnitPath = 
    {   // TODO: add protocol "box:" to this type, in case we need more than one protocol. fjg. 15.10.20
        // TODO: consider adding the occurence range in order to generate better error messages. fjg, 16.02.20
        package: string option
        dirs: string list
        file: string 
    } 
    static member Empty = { package = None; dirs = []; file = "" }
    
    override this.ToString () =
        let prefix = 
            match this.package with 
            | None -> string slash 
            | Some _ -> box
        prefix + (this.AsList |> String.concat (string slash))

    member this.ToDoc = 
        txt <| string this
        
    member this.AsList =
        let path = this.dirs @ [this.file]
        match this.package with
        | None -> path
        | Some pkg -> pkg::path

    member this.GetModuleSegments =
        this.dirs @ [this.file]

    member this.IsOtherBox box =
        this.package <> box


/// Functions =================================================================

/// Given the current path of the translation unit and an import path
/// construct the path of the imported translation unit
// used in SyntaxUtils
let makeFromPath (current: TranslationUnitPath) path : Result<TranslationUnitPath, (string * int)> = 
    let resultBuilder = new ResultBuilder()
    resultBuilder 
        {
            let! nav, (dirs, file) = PathRegex.parseImportPath path
            // Note we do not check whether pkg, dirs and file are valid identifiers here.
            // This is not a parsing issue.
            // Instead, this will be checked when requiring this module. 
            // The search methods would fail there and report the missing module.
            let res = 
                match nav with
                | PathRegex.Pack pkg -> // external package import
                    Ok { package = Some pkg
                         dirs = dirs
                         file = file }
                | PathRegex.Root -> // absolute in-package import
                    Ok { package = current.package 
                         dirs = dirs
                         file = file }
                | PathRegex.Up levels ->
                    if levels <= current.dirs.Length then // in-package import up from current directory
                        Ok { package = current.package
                             dirs = List.take (current.dirs.Length - levels) current.dirs @ dirs
                             file = file }
                    else // too many up steps from current directory
                        let msg =
                            current.dirs 
                            |> String.concat (string slash)
                            |> sprintf "The path ascends %d directory levels up. This is outside the given source path \"%s\"." levels
                        Error (msg, 0)
                | PathRegex.Here ->
                    Ok { package = current.package 
                         dirs = current.dirs @ dirs
                         file = file }
            return! res
        }

/// Returns the list of source directories in a search path.
/// For example: searchPath = ".;../otherSources" -> [".", "../otherSources"] 
let searchPath2Dirs (searchPath: string): string list =
    List.ofArray <| searchPath.Split dirSep    

/// creates a search path template from a directory
let mkTemplate dir suffix = 
    let globPattern = sprintf "%c%s" glob suffix 
    Path.Combine [|dir; globPattern|]

    
/// Calculates a filename from a partial filename and a file path template 'template'.
/// For example: partial filename = "a/b" template = "./?.blc" -> filename = "./a/b.blc" 
let fileName partialFileName template =
    let globToPartialFileName char =
        if char = glob then
            partialFileName
        else 
            string char
    String.collect globToPartialFileName template 


/// Test if file name can be opened for reading
let canOpen fileName =
    try
        let f = File.OpenRead(fileName)
        f.Close()
        true
    with
    | _ -> 
        false


/// Returns the resulting name of the first file with the given suffix in the searchPath that it can open in read mode (after closing it)
/// in case of error it returns a list of file names it tried to open    
let searchFile partialFileName (searchPath: String) extension =
    let dirs = searchPath2Dirs searchPath
    let templates = List.map (fun dir -> mkTemplate dir extension) dirs  
    let filesToTry = List.map (fileName partialFileName) templates
        
    match List.tryFind canOpen filesToTry with
    | Some file ->
        Ok file
    | None ->
        Error filesToTry


let private search searchPath (name: TranslationUnitPath) extension =
    //let partialFileName = replaceSeparator dot Path.DirectorySeparatorChar name 
    let partialFileName = String.concat (string Path.DirectorySeparatorChar) name.AsList
    searchFile partialFileName searchPath extension 

/// Returns the resulting name of the first implementation file in the searchPath that it can open in read mode (after closing it)
/// in case of error it returns a list of file names it tried to open    
let searchImplementation searchPath name = 
    let boxLocalName = { name with package = None }
    search searchPath boxLocalName implementationFileExtension

/// Returns the resulting name of the first interface file in the searchPath that it can open in read mode (after closing it)
/// in case of error it returns a list of file names it tried to open   
let searchInterface searchPath name = 
    search searchPath name interfaceFileExtension

let private tryGetFullPath path = 
    try
        Some <| Path.GetFullPath(path)
    with
    | _ ->
        None


let private isFileInSourceDir file srcDir =
    let fsd = tryGetFullPath srcDir
    let ff = tryGetFullPath file
    match ff, fsd with
    | Some f, Some sd ->
        f.StartsWith sd
    | _, _ ->
        false


/// Given a file (path) and a string representation of directories
/// such as searchPath = ".;../otherSources", return
/// Some path - if file is in this path
/// None - if the file cannot be found in any directory
let tryFindFileInProjectDir file projectDir = 
    searchPath2Dirs projectDir // TODO: Eliminate the possibility to search along a search path, fjg. 10.03.21
    |> List.tryFind (isFileInSourceDir file)


/// Makes a TranslationUnitPath from strings
/// representing the filename and the source dir
/// The result is an Error if the path elements or filename are invalid identifiers
/// such as "blech" or contain non-alphanumerical characters.
let tryMakeTranslationUnitPath file srcDir optPackage : Result<TranslationUnitPath, string list> =
    assert isFileInSourceDir file srcDir
    let ff = Path.GetFullPath file
    let fsd = Path.GetFullPath srcDir

    let relPath = Path.GetRelativePath(fsd, ff).TrimStart([|Path.DirectorySeparatorChar|])
    let dirName= Path.GetDirectoryName relPath
    
    let dirs = if String.IsNullOrEmpty dirName then [] 
               else List.ofSeq <| dirName.Split (Path.DirectorySeparatorChar)
    let file = Path.GetFileNameWithoutExtension ff
    
    let wrongIds = List.filter (fun id -> not <| PathRegex.isValidFileOrDirectoryName id) (dirs @ [file])

    if List.isEmpty wrongIds then 
        Ok { package = optPackage
             dirs = dirs
             file = file }
    else
        // printfn "wrong: %A" wrongIds
        Error wrongIds


/// Filename ends in .blc
let isImplementation (file: string) = 
    (Path.GetExtension file) = implementationFileExtension


/// Filename ends in .blh
let isInterface (file: string) =
    (Path.GetExtension file) = interfaceFileExtension
