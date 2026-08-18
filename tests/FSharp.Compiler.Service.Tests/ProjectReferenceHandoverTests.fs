module FSharp.Compiler.Service.Tests.ProjectReferenceHandoverTests

open Xunit
open System.IO
open System.Reflection
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.IO
open FSharp.Compiler.Service.Tests.Common
open FSharp.Compiler.Symbols
open FSharp.Compiler.TypedTree
open TestFramework

// A referenced project can hand its contents to a consumer already imported instead of pickling them.
// Unpickling is what rewrites ILScopeRef.Local into the referenced assembly, so the handed-over form
// has to do the same: `internal` is a compilation path rooted at Local, and a consumer whose own paths
// are also Local would otherwise read the reference's internals as its own.
//
// Only the background compiler hands contents over, so these pin it rather than taking the suite default.
let private mkChecker share =
    FSharpChecker.Create(
        shareImportedAssemblies = share,
        enablePartialTypeChecking = false,
        useTransparentCompiler = false
    )

let private writeSource (source: string) =
    let fileName = Path.ChangeExtension(getTemporaryFileName (), ".fs")
    FileSystem.OpenFileForWriteShim(fileName).Write(source)
    fileName

/// Options for one project, referencing the given already-built ones
let private projectOptions (checker: FSharpChecker) source references extraOptions =
    let fileNames = [| writeSource source |]
    let baseName = getTemporaryFileName ()
    let dllName = Path.ChangeExtension(baseName, ".dll")
    let projName = Path.ChangeExtension(baseName, ".fsproj")
    let args = mkProjectCommandLineArgsSilent (dllName, fileNames)
    let options = checker.GetProjectOptionsFromCommandLineArgs(projName, args)

    let options =
        { options with
            SourceFiles = fileNames
            OtherOptions =
                Array.concat
                    [ options.OtherOptions
                      [| for dll, _ in references -> "-r:" + dll |]
                      extraOptions ]
            ReferencedProjects =
                [| for dll, opts in references -> FSharpReferencedProject.FSharpReference(dll, opts) |] }

    dllName, options

/// FSharpAssembly holds the ccu it wraps but does not expose it
let private ccuOf (assembly: FSharpAssembly) =
    assembly.GetType().GetFields(BindingFlags.Instance ||| BindingFlags.NonPublic ||| BindingFlags.Public)
    |> Array.pick (fun field ->
        match field.GetValue assembly with
        | :? CcuThunk as ccu -> Some ccu
        | _ -> None)

/// The ccu the given project ended up with for one of its references
let private referencedCcu (checker: FSharpChecker) (options: FSharpProjectOptions) name =
    let results = checker.ParseAndCheckProject options |> Async.RunSynchronously

    results.ProjectContext.GetReferencedAssemblies()
    |> List.find (fun assembly -> assembly.SimpleName = name)
    |> ccuOf

let private errorsIn (checker: FSharpChecker) (options: FSharpProjectOptions) =
    let results = checker.ParseAndCheckProject options |> Async.RunSynchronously

    results.Diagnostics
    |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)

let private librarySource =
    """
module Library

let publicValue = 1
let internal secretValue = 2
type internal SecretType = { S: int }
type Colour = internal Red | Green
type internal SecretClass() =
    member _.M = 3
"""

let private consumerOf body = "module Consumer\n" + body + "\n"

/// Enough on its own where the shape of the internal does not matter
let private useInternalValue = "let useValue = Library.secretValue"

[<Theory>]
[<InlineData "let useValue = Library.secretValue">]
[<InlineData "let useType = typeof<Library.SecretType>">]
[<InlineData "let useCase = Library.Red">]
[<InlineData "let useClass = Library.SecretClass().M">]
let ``An internal of a handed-over project is not visible to a consumer`` (useInternal: string) =
    let checker = mkChecker true
    let library = projectOptions checker librarySource [] [||]
    let _, consumer = projectOptions checker (consumerOf useInternal) [ library ] [||]

    Assert.NotEmpty(errorsIn checker consumer)

[<Fact>]
let ``Handed-over contents keep the public surface usable`` () =
    let checker = mkChecker true
    let library = projectOptions checker librarySource [] [||]

    let _, consumer =
        projectOptions checker (consumerOf "let usePublic = Library.publicValue") [ library ] [||]

    Assert.Empty(errorsIn checker consumer)

[<Fact>]
let ``A project handed to one consumer is still pickled correctly for another`` () =
    let checker = mkChecker true
    let library = projectOptions checker librarySource [] [||]

    // The first consumer shares the library's TcGlobals and takes the handed-over contents. The second
    // is on another language version, so its TcGlobals differs, CanImportInto refuses, and it unpickles.
    // Checking that one second is what would expose the handover's in-place pruning as shared state.
    let _, sharing = projectOptions checker (consumerOf useInternalValue) [ library ] [||]

    let _, unpickling =
        projectOptions checker (consumerOf useInternalValue) [ library ] [| "--langversion:8.0" |]

    Assert.NotEmpty(errorsIn checker sharing)
    Assert.NotEmpty(errorsIn checker unpickling)

[<Fact>]
let ``Type identity survives a chain of handed-over project references`` () =
    let checker = mkChecker true

    let leaf =
        projectOptions
            checker
            """
module Leaf

let publicLeaf = 1
let internal secretLeaf = 2
type LeafType = { X: int }
"""
            []
            [||]

    let middle =
        projectOptions
            checker
            """
module Middle

let publicMiddle = Leaf.publicLeaf
let makeLeafType () : Leaf.LeafType = { X = 1 }
"""
            [ leaf ]
            [||]

    // As a build passes project references on transitively
    let _, consumer =
        projectOptions
            checker
            """
module Consumer

let useMiddle = Middle.publicMiddle
let useLeafThroughMiddle = (Middle.makeLeafType ()).X
let useLeafDirectly : Leaf.LeafType = { X = 2 }
"""
            [ middle; leaf ]
            [||]

    // The leaf type reached through the middle project must be the one the consumer imports itself
    Assert.Empty(errorsIn checker consumer)

/// Handing the contents over is what makes one imported form serve every consumer. Were the handover
/// to stop happening these would still type-check, so this is what holds the feature in place.
[<Theory>]
[<InlineData true>]
[<InlineData false>]
let ``Consumers share one imported ccu only when contents are handed over`` share =
    let checker = mkChecker share
    let library = projectOptions checker librarySource [] [||]
    let libraryName = Path.GetFileNameWithoutExtension(fst library)

    let _, first =
        projectOptions checker (consumerOf "let first = Library.publicValue") [ library ] [||]

    let _, second =
        projectOptions checker (consumerOf "let second = Library.publicValue") [ library ] [||]

    let firstCcu = referencedCcu checker first libraryName
    let secondCcu = referencedCcu checker second libraryName

    if share then
        Assert.Same(firstCcu, secondCcu)
    else
        Assert.NotSame(firstCcu, secondCcu)
