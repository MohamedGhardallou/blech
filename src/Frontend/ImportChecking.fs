﻿// Copyright (c) 2020 - for information on the respective copyright owner
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



module Blech.Frontend.ImportChecking

open System.Collections.Generic

open Blech.Common
open Blech.Frontend


type private TranslationUnitPath = TranslationUnitPath.TranslationUnitPath
type private Environment = SymbolTable.Environment
type private Singletons = OpaqueInference.Singletons
type private AbstractTypes = OpaqueInference.AbstractTypes

type ModuleInfo = 
    {
        dependsOn: TranslationUnitPath list
        moduleSpec : AST.ModuleSpec option
        nameCheck: Environment
        singletons : Singletons
        abstractTypes : AbstractTypes
        typeCheck: TypeCheckContext
        typedModule: BlechTypes.BlechModule
    }

    static member Make imports
                       moduleSpec
                       symbolTable 
                       singletons
                       abstractTypes
                       typecheckContext 
                       blechModule =

        { 
            dependsOn = imports
            moduleSpec = moduleSpec
            nameCheck = symbolTable
            singletons = singletons
            abstractTypes = abstractTypes
            typeCheck = typecheckContext
            typedModule = blechModule
        }

    member this.GetModuleName : TranslationUnitPath = 
        this.typedModule.name

    member this.GetEnv : Environment = 
        this.nameCheck

    member this.GetExportScope : SymbolTable.Scope = 
        SymbolTable.Environment.getModuleScope this.nameCheck

    member this.IsProgram = 
        Option.isNone this.moduleSpec

    member this.IsInternal = 
        match this.moduleSpec with
        | Some spec ->
            spec.isInternal
        | None ->
            false
    
type ImportError = 
    | CyclicImport of range: Range.range * path: TranslationUnitPath
    | MultipleImport of range: Range.range * path: TranslationUnitPath
    | ProgramImport of range: Range.range * path: TranslationUnitPath
    | IllegalWhiteboxImport of range: Range.range * libraryPath: TranslationUnitPath
    | IllegalImportOfInternal of range: Range.range * libraryPath: TranslationUnitPath
    | CannotCompileImport of range: Range.range * path: TranslationUnitPath
    | Dummy of range: Range.range * msg: string   // just for development purposes

    interface Diagnostics.IDiagnosable with
        
        member err.MainInformation =
            match err with
            | CyclicImport (rng, path) ->
                { range = rng 
                  message = sprintf "the import of module '%s' is cyclic" <| string path }
            | MultipleImport (rng, path) ->
                { range = rng 
                  message = sprintf "the module '%s' is imported more than once" <| string path }
            | ProgramImport (rng, path) ->
                { range = rng 
                  message = sprintf "the import '%s' is a program and cannot be imported" <| string path }
            | IllegalWhiteboxImport (rng, path) ->
                { range = rng 
                  message = sprintf "whitebox import for any library module, like \"%s\", is not allowed"  <| string path }
            | IllegalImportOfInternal (rng, path) ->
                { range = rng 
                  message = sprintf "import for any internal library module, like \"%s\", is not allowed"  <| string path }
            | CannotCompileImport (rng, path) ->
                { range = rng 
                  message = sprintf "cannot compile import '%s'" <| string path }
            | Dummy (rng, msg) ->
                { range = rng
                  message = sprintf "Dummy error: %s" msg }

        member err.ContextInformation  = 
            match err with
            | CyclicImport (range = rng) ->
                [ { range = rng; message = "cyclic import"; isPrimary = true } ]
            | MultipleImport (range = rng) ->
                [ { range = rng; message = "multiple import"; isPrimary = true } ]
            | ProgramImport (range = rng) ->
                [ { range = rng; message = "program import"; isPrimary = true } ]
            | IllegalWhiteboxImport (range = rng) ->
                [ { range = rng; message = "library import"; isPrimary = true } ]
            | IllegalImportOfInternal (range = rng) ->
                [ { range = rng; message = "library internal"; isPrimary = true } ]
            | CannotCompileImport (range = rng) ->
                [ { range = rng; message = "not compileable"; isPrimary = true } ]
            | Dummy (range = rng) ->
                [ { range = rng; message = "thats wrong"; isPrimary = true } ]

        member err.NoteInformation = []


// type private Logger = Diagnostics.Logger

type Imports = 
    private {
        // imports: TranslationUnitPath list
        moduleName: TranslationUnitPath
        importChain: CompilationUnit.ImportChain
        importPaths: Set<TranslationUnitPath>
        compiledImports: Dictionary<TranslationUnitPath, ModuleInfo>
    }
    
    static member Initialise (importChain : CompilationUnit.ImportChain) moduleName = 
        { moduleName = moduleName
          importChain = importChain.Extend moduleName // add importing module to import chain, to detect self import
          importPaths = Set.empty
          compiledImports = Dictionary() }
    
    member this.ExtendImportChain moduleName = 
        { this with importChain = this.importChain.Extend moduleName }

    member this.GetImportedModuleNames : TranslationUnitPath list = 
        Seq.toList ( this.compiledImports.Keys )

    member this.AddCompiledImport moduleName moduleInfo =
        ignore <| this.compiledImports.TryAdd(moduleName, moduleInfo)  // The same module might be added more than once
        this

    member this.AddImportPath moduleName =
           { this with importPaths = this.importPaths.Add moduleName }

    member this.GetImports : ModuleInfo list = 
        Seq.toList this.compiledImports.Values

    member this.GetLookupTables : Map<TranslationUnitPath, SymbolTable.LookupTable> = 
        Map.ofList [ for pair in this.compiledImports do yield (pair.Key, pair.Value.GetEnv.GetLookupTable) ]

    member this.GetExportScopes : Map<TranslationUnitPath, SymbolTable.Scope> = 
        Map.ofList [ for pair in this.compiledImports do yield (pair.Key, pair.Value.GetExportScope) ]
        
    member this.GetSingletons : OpaqueInference.Singletons list = 
        this.GetImports
        |> List.map (fun import -> import.singletons)

    member this.GetAbstractTypes : OpaqueInference.AbstractTypes list = 
        this.GetImports
        |> List.map (fun import -> import.abstractTypes)

    member this.GetImportedInternalModules : ExportInference.ImportedInternalModules =
        Set <| seq { for pair in this.compiledImports 
                        do if pair.Value.IsInternal then yield pair.Key }
        
    member this.GetTypeCheckContexts : TypeCheckContext list =
        this.GetImports
        |> List.map (fun i -> i.typeCheck)

    member this.GetTypedModules : BlechTypes.BlechModule list = 
        this.GetImports
        |> List.map (fun i -> i.typedModule)


// check if there is a cylic module imported, i.e. 
// the module to import is already contained in the chain of module imports
// this also handles self import
let private checkCyclicImport (importedModule: AST.ModulePath) logger (imports: Imports) =
    let modName = importedModule.path
    let srcRng = importedModule.Range
    if imports.importChain.Contains importedModule.path then
        CyclicImport (srcRng, modName)
        |> Diagnostics.Logger.logError logger Diagnostics.Phase.Importing
        Error logger
    else
        Ok imports


// check if a module is imported multiple times 
let private checkMultipleImport (pkgCtx : CompilationUnit.Context<ModuleInfo>) (importedModule: AST.ModulePath) logger (imports: Imports) =
    let modName = importedModule.path
    let srcRng = importedModule.Range
    // printfn "Check multiple import: %s" <| string modName
    if imports.importPaths.Contains modName then
        MultipleImport (srcRng, modName)
        |> Diagnostics.Logger.logError logger Diagnostics.Phase.Importing
        Error logger
    else
        Ok <| imports.AddImportPath modName
        

// check if the imported and compiled module is NOT a program
let private checkImportIsNotAProgram logger (modul: AST.ModulePath) (compiledModule: CompilationUnit.Module<ModuleInfo>) (imports: Imports) =
    let modName = modul.path
    let srcRng = modul.Range
    // printfn "Check import is not a program"
    if compiledModule.info.IsProgram then
        ProgramImport (srcRng, modName)
        |> Diagnostics.Logger.logError logger Diagnostics.Phase.Importing
        Error logger
    else
        Ok <| imports.AddCompiledImport modul.path compiledModule.info


// checks if the whitebox import of a module is not from another box
let private checkWhiteboxImport logger box (import: AST.Import) (imports: Imports) =
    let modpath = import.modulePath
    if import.isInternal && (modpath.path.IsOtherBox box) then
        // TODO: Currently this cannot be tested, because we cannot import from another package, fjg. 09.03.21
        IllegalWhiteboxImport (modpath.range, modpath.path)
        |> Diagnostics.Logger.logError logger Diagnostics.Phase.Importing
        Error logger
    else
        Ok imports

        
// checks if the already compiled import of an internal module is not from another box
let checkImportofInternalModule logger box (importPath : AST.ModulePath) (imports : Imports) =
    assert imports.compiledImports.ContainsKey importPath.path // this will only be called after a successful compilation
    let compiledModule = imports.compiledImports.Item importPath.path
    if compiledModule.IsInternal && importPath.path.IsOtherBox box then
        // TODO: Currently this cannot be tested, because we cannot import from another package, fjg. 09.03.21
        IllegalImportOfInternal (importPath.range, importPath.path)
        |> Diagnostics.Logger.logError logger Diagnostics.Phase.Importing
        Error logger    
    else
        Ok imports


// tries to compile an imported module
// if successful, adds it to the collection of compiled imported modules.
// else logs an error for the importing module.
let private compileImportedModule pkgCtx logger (importPath: AST.ModulePath) importInternal (imports: Imports)  = 
    let modName = importPath.path
    let srcRng = importPath.Range
    
    let freshLogger = Diagnostics.Logger.create ()
    let importChain = imports.importChain.Extend modName
    let compRes = CompilationUnit.require pkgCtx freshLogger importChain modName srcRng importInternal
    
    match compRes with
    | Ok compiledModule ->
        checkImportIsNotAProgram logger importPath compiledModule imports // log error to the importing module's logger
        |> Result.bind (checkImportofInternalModule logger pkgCtx.box importPath)
    | Error _ ->
        CannotCompileImport (srcRng, modName)
        |> Diagnostics.Logger.logError logger Diagnostics.Phase.Importing
        Error logger


// Check one import
let private checkImport (pkgCtx : CompilationUnit.Context<ModuleInfo>) 
                        logger 
                        (imports : Imports) 
                        (import : AST.Import) : Imports = 
    let returnImports = function 
    | Ok updatedImports -> updatedImports
    | Error _ -> imports
    
    checkCyclicImport import.modulePath logger imports
    |> Result.bind (checkMultipleImport pkgCtx import.modulePath logger)
    |> Result.bind (checkWhiteboxImport logger pkgCtx.box import)
    |> Result.bind (compileImportedModule pkgCtx logger import.modulePath import.isInternal)
    |> returnImports 
    

// Check all imports one after another
// go on even if an import is not compilable
let checkImports (pktCtx: CompilationUnit.Context<ModuleInfo>) 
                 logger 
                 (importChain : CompilationUnit.ImportChain) 
                 moduleName 
                 (compUnit: AST.CompilationUnit) 
        : Result<Imports, Diagnostics.Logger> = 
    // Compile all imported modules, regardless of compilation errors
    let imports = 
        List.fold (checkImport pktCtx logger) (Imports.Initialise importChain moduleName) compUnit.imports
    
    // Return Error if at least one imported module could not be compiled
    if Diagnostics.Logger.hasErrors logger then
        Error logger
    else 
        Ok imports
