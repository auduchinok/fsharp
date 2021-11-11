// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

// Type providers, validation of provided types, etc.

namespace FSharp.Compiler

#if !NO_EXTENSIONTYPING

open System
open System.Collections.Concurrent
open System.IO
open System.Collections.Generic
open System.Reflection
open Internal.Utilities.Library
open Internal.Utilities.FSharpEnvironment  
open FSharp.Core.CompilerServices
open FSharp.Compiler.AbstractIL.IL
open FSharp.Compiler.ErrorLogger
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text
open FSharp.Compiler.Text.Range

module ExtensionTyping =

    type TypeProviderDesignation = TypeProviderDesignation of string

    exception ProvidedTypeResolution of range * System.Exception
    exception ProvidedTypeResolutionNoRange of System.Exception

    let toolingCompatiblePaths() = toolingCompatiblePaths ()

    /// Represents some of the configuration parameters passed to type provider components 
    type ResolutionEnvironment =
        { resolutionFolder: string
          outputFile: string option
          showResolutionMessages: bool
          referencedAssemblies: string[]
          temporaryFolder: string }

    /// Load a the design-time part of a type-provider into the host process, and look for types
    /// marked with the TypeProviderAttribute attribute.
    let GetTypeProviderImplementationTypes (runTimeAssemblyFileName, designTimeAssemblyNameString, m:range, compilerToolPaths:string list) =

        // Report an error, blaming the particular type provider component
        let raiseError designTimeAssemblyPathOpt (e: exn) =
            let attrName = typeof<TypeProviderAssemblyAttribute>.Name
            let exnTypeName = e.GetType().FullName
            let exnMsg = e.Message
            match designTimeAssemblyPathOpt with 
            | None -> 
                let msg = FSComp.SR.etProviderHasWrongDesignerAssemblyNoPath(attrName, designTimeAssemblyNameString, exnTypeName, exnMsg)
                raise (TypeProviderError(msg, runTimeAssemblyFileName, m))
            | Some designTimeAssemblyPath -> 
                let msg = FSComp.SR.etProviderHasWrongDesignerAssembly(attrName, designTimeAssemblyNameString, designTimeAssemblyPath, exnTypeName, exnMsg)
                raise (TypeProviderError(msg, runTimeAssemblyFileName, m))

        let designTimeAssemblyOpt = getTypeProviderAssembly (runTimeAssemblyFileName, designTimeAssemblyNameString, compilerToolPaths, raiseError)

        match designTimeAssemblyOpt with
        | Some loadedDesignTimeAssembly ->
            try
                let exportedTypes = loadedDesignTimeAssembly.GetExportedTypes() 
                let filtered = 
                    [ for t in exportedTypes do 
                          let ca = t.GetCustomAttributes(typeof<TypeProviderAttribute>, true)
                          if ca <> null && ca.Length > 0 then 
                              yield t ]
                filtered
            with e ->
                let folder = Path.GetDirectoryName loadedDesignTimeAssembly.Location
                let exnTypeName = e.GetType().FullName
                let exnMsg = e.Message
                match e with 
                | :? FileLoadException -> 
                    let msg = FSComp.SR.etProviderHasDesignerAssemblyDependency(designTimeAssemblyNameString, folder, exnTypeName, exnMsg)
                    raise (TypeProviderError(msg, runTimeAssemblyFileName, m))
                
                | _ -> 
                    let msg = FSComp.SR.etProviderHasDesignerAssemblyException(designTimeAssemblyNameString, folder, exnTypeName, exnMsg)
                    raise (TypeProviderError(msg, runTimeAssemblyFileName, m))
        | None -> []

    let StripException (e: exn) =
        match e with
        |   :? TargetInvocationException as e -> e.InnerException
        |   :? TypeInitializationException as e -> e.InnerException
        |   _ -> e

    /// Create an instance of a type provider from the implementation type for the type provider in the
    /// design-time assembly by using reflection-invoke on a constructor for the type provider.
    let CreateTypeProvider (typeProviderImplementationType: Type, 
                            runtimeAssemblyPath, 
                            resolutionEnvironment: ResolutionEnvironment, 
                            isInvalidationSupported: bool, 
                            isInteractive: bool, 
                            systemRuntimeContainsType, 
                            systemRuntimeAssemblyVersion, 
                            m) =

        // Protect a .NET reflection call as we load the type provider component into the host process, 
        // reporting errors.
        let protect f =
            try 
                f ()
            with err ->
                let e = StripException (StripException err)
                raise (TypeProviderError(FSComp.SR.etTypeProviderConstructorException(e.Message), typeProviderImplementationType.FullName, m))

        if typeProviderImplementationType.GetConstructor([| typeof<TypeProviderConfig> |]) <> null then

            // Create the TypeProviderConfig to pass to the type provider constructor
            let e = TypeProviderConfig(systemRuntimeContainsType, 
                                       ResolutionFolder=resolutionEnvironment.resolutionFolder, 
                                       RuntimeAssembly=runtimeAssemblyPath, 
                                       ReferencedAssemblies=Array.copy resolutionEnvironment.referencedAssemblies, 
                                       TemporaryFolder=resolutionEnvironment.temporaryFolder, 
                                       IsInvalidationSupported=isInvalidationSupported, 
                                       IsHostedExecution= isInteractive, 
                                       SystemRuntimeAssemblyVersion = systemRuntimeAssemblyVersion)

            protect (fun () -> Activator.CreateInstance(typeProviderImplementationType, [| box e|]) :?> ITypeProvider )

        elif typeProviderImplementationType.GetConstructor [| |] <> null then 
            protect (fun () -> Activator.CreateInstance typeProviderImplementationType :?> ITypeProvider )

        else
            // No appropriate constructor found
            raise (TypeProviderError(FSComp.SR.etProviderDoesNotHaveValidConstructor(), typeProviderImplementationType.FullName, m))

    let GetTypeProvidersOfAssemblyInternal
            (runtimeAssemblyFilename: string,
             designTimeName: string, 
             resolutionEnvironment: ResolutionEnvironment, 
             isInvalidationSupported: bool, 
             isInteractive: bool, 
             systemRuntimeContainsType: string -> bool, 
             systemRuntimeAssemblyVersion: Version, 
             compilerToolPaths: string list,
             logError: TypeProviderError -> unit,
             m:range) =

        let providers =
                try
                    let designTimeAssemblyName = 
                        try
                            if designTimeName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) then
                                Some (AssemblyName (Path.GetFileNameWithoutExtension designTimeName))
                            else
                                Some (AssemblyName designTimeName)
                        with :? ArgumentException ->
                            errorR(Error(FSComp.SR.etInvalidTypeProviderAssemblyName(runtimeAssemblyFilename, designTimeName), m))
                            None

                    [ match designTimeAssemblyName, resolutionEnvironment.outputFile with
                      // Check if the attribute is pointing to the file being compiled, in which case ignore it
                      // This checks seems like legacy but is included for compat.
                      | Some designTimeAssemblyName, Some path 
                         when String.Compare(designTimeAssemblyName.Name, Path.GetFileNameWithoutExtension path, StringComparison.OrdinalIgnoreCase) = 0 ->
                          ()

                      | Some _, _ ->
                          let provImplTypes = GetTypeProviderImplementationTypes (runtimeAssemblyFilename, designTimeName, m, compilerToolPaths)
                          for t in provImplTypes do
                            let resolver =
                                CreateTypeProvider (t, runtimeAssemblyFilename, resolutionEnvironment, isInvalidationSupported,
                                    isInteractive, systemRuntimeContainsType, systemRuntimeAssemblyVersion, m)
                            match box resolver with 
                            | null -> ()
                            | _ -> yield resolver

                      |   None, _ -> 
                          () ]

                with :? TypeProviderError as tpe ->
                    logError tpe
                    []

        providers

    let unmarshal (t: Tainted<_>) = t.PUntaintNoFailure id

    /// Try to access a member on a provided type, catching and reporting errors
    let TryTypeMember(st: Tainted<_>, fullName, memberName, m, recover, f) =
        try
            st.PApply (f, m)
        with :? TypeProviderError as tpe -> 
            tpe.Iter (fun e -> errorR(Error(FSComp.SR.etUnexpectedExceptionFromProvidedTypeMember(fullName, memberName, e.ContextualErrorMessage), m)))
            st.PApplyNoFailure(fun _ -> recover)

    /// Try to access a member on a provided type, where the result is an array of values, catching and reporting errors
    let TryTypeMemberArray (st: Tainted<_>, fullName, memberName, m, f) =
        let result =
            try
                st.PApplyArray(f, memberName, m)
            with :? TypeProviderError as tpe ->
                tpe.Iter (fun e -> error(Error(FSComp.SR.etUnexpectedExceptionFromProvidedTypeMember(fullName, memberName, e.ContextualErrorMessage), m)))
                [||]

        match result with 
        | null -> error(Error(FSComp.SR.etUnexpectedNullFromProvidedTypeMember(fullName, memberName), m)); [||]
        | r -> r

    /// Try to access a member on a provided type, catching and reporting errors and checking the result is non-null, 
    let TryTypeMemberNonNull (st: Tainted<_>, fullName, memberName, m, recover, f) =
        match TryTypeMember(st, fullName, memberName, m, recover, f) with 
        | Tainted.Null -> 
            errorR(Error(FSComp.SR.etUnexpectedNullFromProvidedTypeMember(fullName, memberName), m)); 
            st.PApplyNoFailure(fun _ -> recover)
        | r -> r

    /// Try to access a property or method on a provided member, catching and reporting errors
    let TryMemberMember (mi: Tainted<_>, typeName, memberName, memberMemberName, m, recover, f) = 
        try
            mi.PApply (f, m)
        with :? TypeProviderError as tpe ->
            tpe.Iter (fun e -> errorR(Error(FSComp.SR.etUnexpectedExceptionFromProvidedMemberMember(memberMemberName, typeName, memberName, e.ContextualErrorMessage), m)))
            mi.PApplyNoFailure(fun _ -> recover)

    /// Validate a provided namespace name
    let ValidateNamespaceName(name, typeProvider: Tainted<ITypeProvider>, m, nsp: string) =
        if nsp<>null then // Null namespace designates the global namespace.
            if String.IsNullOrWhiteSpace nsp then
                // Empty namespace is not allowed
                errorR(Error(FSComp.SR.etEmptyNamespaceOfTypeNotAllowed(name, typeProvider.PUntaint((fun tp -> tp.GetType().Name), m)), m))
            else
                for s in nsp.Split('.') do
                    match s.IndexOfAny(PrettyNaming.IllegalCharactersInTypeAndNamespaceNames) with
                    | -1 -> ()
                    | n -> errorR(Error(FSComp.SR.etIllegalCharactersInNamespaceName(string s.[n], s), m))  

    let bindingFlags =
        BindingFlags.DeclaredOnly |||
        BindingFlags.Static |||
        BindingFlags.Instance |||
        BindingFlags.Public

    type CustomAttributeData = System.Reflection.CustomAttributeData
    type CustomAttributeNamedArgument = System.Reflection.CustomAttributeNamedArgument
    type CustomAttributeTypedArgument = System.Reflection.CustomAttributeTypedArgument

    // NOTE: for the purposes of remapping the closure of generated types, the FullName is sufficient.
    // We do _not_ rely on object identity or any other notion of equivalence provided by System.Type
    // itself. The mscorlib implementations of System.Type equality relations are not suitable: for
    // example RuntimeType overrides the equality relation to be reference equality for the Equals(object)
    // override, but the other subtypes of System.Type do not, making the relation non-reflective.
    //
    // Further, avoiding reliance on canonicalization (UnderlyingSystemType) or System.Type object identity means that 
    // providers can implement wrap-and-filter "views" over existing System.Type clusters without needing
    // to preserve object identity when presenting the types to the F# compiler.

    type ProvidedTypeComparer() = 
        let key (ty: ProvidedType) = (ty.Assembly.FullName, ty.FullName)
        static member val Instance = ProvidedTypeComparer()
        interface IEqualityComparer<ProvidedType> with
            member _.GetHashCode(ty: ProvidedType) = hash (key ty)
            member _.Equals(ty1: ProvidedType, ty2: ProvidedType) = (key ty1 = key ty2)

    /// The context used to interpret information in the closure of System.Type, System.MethodInfo and other 
    /// info objects coming from the type provider.
    ///
    /// This is the "Type --> Tycon" remapping context of the type. This is only present for generated provided types, and contains
    /// all the entries in the remappings for the generative declaration.
    ///
    /// Laziness is used "to prevent needless computation for every type during remapping". However it
    /// appears that the laziness likely serves no purpose and could be safely removed.
    and ProvidedTypeContext = 
        | NoEntries
        // The dictionaries are safe because the ProvidedType with the ProvidedTypeContext are only accessed one thread at a time during type-checking.
        | Entries of ConcurrentDictionary<ProvidedType, ILTypeRef> * Lazy<ConcurrentDictionary<ProvidedType, obj>>

        static member Empty = NoEntries

        static member Create(d1, d2) = Entries(d1, notlazy d2)

        member ctxt.GetDictionaries()  = 
            match ctxt with
            | NoEntries -> 
                ConcurrentDictionary<ProvidedType, ILTypeRef>(ProvidedTypeComparer.Instance), ConcurrentDictionary<ProvidedType, obj>(ProvidedTypeComparer.Instance)
            | Entries (lookupILTR, lookupILTCR) ->
                lookupILTR, lookupILTCR.Force()

        member ctxt.TryGetILTypeRef st = 
            match ctxt with 
            | NoEntries -> None 
            | Entries(d, _) -> 
                match d.TryGetValue st with
                | true, res -> Some res
                | _ -> None

        member ctxt.TryGetTyconRef st = 
            match ctxt with 
            | NoEntries -> None 
            | Entries(_, d) -> 
                let d = d.Force()
                match d.TryGetValue st with
                | true, res -> Some res
                | _ -> None

        member ctxt.RemapTyconRefs (f: obj->obj) = 
            match ctxt with 
            | NoEntries -> NoEntries
            | Entries(d1, d2) ->
                Entries(d1, lazy (let dict = ConcurrentDictionary<ProvidedType, obj>(ProvidedTypeComparer.Instance)
                                  for KeyValue (st, tcref) in d2.Force() do dict.TryAdd(st, f tcref) |> ignore
                                  dict))

    and [<AllowNullLiteral>]
        ProvidedType (x: Type, ctxt: ProvidedTypeContext) =
        inherit ProvidedMemberInfo(x, ctxt)
        let isMeasure = 
            lazy
                x.CustomAttributes 
                |> Seq.exists (fun a -> a.Constructor.DeclaringType.FullName = typeof<MeasureAttribute>.FullName)
        
        // The type provider spec distinguishes between 
        //   - calls that can be made on provided types (i.e. types given by ReturnType, ParameterType, and generic argument types)
        //   - calls that can be made on provided type definitions (types returned by ResolveTypeName, GetTypes etc.)
        // Ideally we would enforce this decision structurally by having both ProvidedType and ProvidedTypeDefinition.
        // Alternatively we could use assertions to enforce this.

        // Suppress relocation of generated types
        abstract member IsSuppressRelocate: bool
        default __.IsSuppressRelocate = (x.Attributes &&& enum (int32 TypeProviderTypeAttributes.SuppressRelocate)) <> enum 0  
        abstract member IsErased: bool
        default __.IsErased = (x.Attributes &&& enum (int32 TypeProviderTypeAttributes.IsErased)) <> enum 0  
        abstract member IsGenericType: bool
        default __.IsGenericType = x.IsGenericType
        abstract member Namespace: string
        default __.Namespace = x.Namespace
        abstract member FullName: string
        default __.FullName = x.FullName
        abstract member IsArray: bool
        default __.IsArray = x.IsArray
        abstract member Assembly: ProvidedAssembly
        default __.Assembly = x.Assembly |> ProvidedAssembly.Create
        abstract member GetInterfaces: unit -> ProvidedType[]
        default __.GetInterfaces() = x.GetInterfaces() |> ProvidedType.CreateArray ctxt
        abstract member GetMethods: unit -> ProvidedMethodInfo[]
        default __.GetMethods() = x.GetMethods bindingFlags |> ProvidedMethodInfo.CreateArray ctxt
        abstract member GetEvents: unit -> ProvidedEventInfo[]
        default __.GetEvents() = x.GetEvents bindingFlags |> ProvidedEventInfo.CreateArray ctxt
        abstract member GetEvent: nm: string -> ProvidedEventInfo
        default __.GetEvent nm = x.GetEvent(nm, bindingFlags) |> ProvidedEventInfo.Create ctxt
        abstract member GetProperties: unit -> ProvidedPropertyInfo[]
        default __.GetProperties() = x.GetProperties bindingFlags |> ProvidedPropertyInfo.CreateArray ctxt
        abstract member GetProperty: string -> ProvidedPropertyInfo
        default __.GetProperty nm = x.GetProperty(nm, bindingFlags) |> ProvidedPropertyInfo.Create ctxt
        abstract member GetConstructors: unit -> ProvidedConstructorInfo[]
        default __.GetConstructors() = x.GetConstructors bindingFlags |> ProvidedConstructorInfo.CreateArray ctxt
        abstract GetFields: unit -> ProvidedFieldInfo[]
        default __.GetFields() = x.GetFields bindingFlags |> ProvidedFieldInfo.CreateArray ctxt
        abstract GetField: nm: string -> ProvidedFieldInfo
        default __.GetField nm = x.GetField(nm, bindingFlags) |> ProvidedFieldInfo.Create ctxt
        abstract member GetAllNestedTypes: unit -> ProvidedType[]
        default __.GetAllNestedTypes() = x.GetNestedTypes(bindingFlags ||| BindingFlags.NonPublic) |> ProvidedType.CreateArray ctxt
        abstract member GetNestedTypes: unit -> ProvidedType[]
        default __.GetNestedTypes() = x.GetNestedTypes bindingFlags |> ProvidedType.CreateArray ctxt
        /// Type.GetNestedType(string) can return null if there is no nested type with given name
        abstract member GetNestedType: nm: string -> ProvidedType
        default __.GetNestedType nm = x.GetNestedType (nm, bindingFlags) |> ProvidedType.Create ctxt
        /// Type.GetGenericTypeDefinition() either returns type or throws exception, null is not permitted
        abstract member GetGenericTypeDefinition: unit -> ProvidedType
        default __.GetGenericTypeDefinition() = x.GetGenericTypeDefinition() |> ProvidedType.CreateWithNullCheck ctxt "GenericTypeDefinition"
        /// Type.BaseType can be null when Type is interface or object
        abstract member BaseType: ProvidedType
        default __.BaseType = x.BaseType |> ProvidedType.Create ctxt
        abstract member GetStaticParameters: ITypeProvider -> ProvidedParameterInfo[]
        default __.GetStaticParameters(provider: ITypeProvider) = provider.GetStaticParameters x |> ProvidedParameterInfo.CreateArray ctxt
        /// Type.GetElementType can be null if i.e. Type is not array\pointer\byref type
        abstract member GetElementType: unit -> ProvidedType
        default __.GetElementType() = x.GetElementType() |> ProvidedType.Create ctxt
        abstract member GetGenericArguments: unit -> ProvidedType[]
        default __.GetGenericArguments() = x.GetGenericArguments() |> ProvidedType.CreateArray ctxt
        abstract member ApplyStaticArguments: ITypeProvider * string[] * obj[] -> ProvidedType
        default __.ApplyStaticArguments(provider: ITypeProvider, fullTypePathAfterArguments, staticArgs: obj[]) = 
            provider.ApplyStaticArguments(x, fullTypePathAfterArguments,  staticArgs) |> ProvidedType.Create ctxt
        abstract member IsVoid: bool
        default __.IsVoid = (typeof<Void>.Equals x || (x.Namespace = "System" && x.Name = "Void"))
        abstract member IsGenericParameter: bool
        default __.IsGenericParameter = x.IsGenericParameter
        abstract member IsValueType: bool
        default __.IsValueType = x.IsValueType
        abstract member IsByRef: bool
        default __.IsByRef = x.IsByRef
        abstract member IsPointer: bool
        default __.IsPointer = x.IsPointer
        abstract member IsPublic: bool
        default __.IsPublic = x.IsPublic
        abstract member IsNestedPublic: bool
        default __.IsNestedPublic = x.IsNestedPublic
        abstract member IsEnum: bool
        default __.IsEnum = x.IsEnum
        abstract member IsClass: bool
        default __.IsClass = x.IsClass
        abstract member IsMeasure: bool
        default __.IsMeasure = isMeasure.Value
        abstract member IsSealed: bool
        default __.IsSealed = x.IsSealed
        abstract member IsAbstract: bool
        default __.IsAbstract = x.IsAbstract
        abstract member IsInterface: bool
        default __.IsInterface = x.IsInterface
        abstract member GetArrayRank: unit -> int
        default __.GetArrayRank() = x.GetArrayRank()
        abstract member GenericParameterPosition: int 
        default __.GenericParameterPosition = x.GenericParameterPosition
        member __.RawSystemType = x
        /// Type.GetEnumUnderlyingType either returns type or raises exception, null is not permitted
        abstract member GetEnumUnderlyingType: unit -> ProvidedType
        default __.GetEnumUnderlyingType() = 
            x.GetEnumUnderlyingType() 
            |> ProvidedType.CreateWithNullCheck ctxt "EnumUnderlyingType"
        abstract member MakePointerType: unit -> ProvidedType
        default __.MakePointerType() = ProvidedType.CreateNoContext(x.MakePointerType())
        abstract member MakeByRefType: unit -> ProvidedType
        default __.MakeByRefType() = ProvidedType.CreateNoContext(x.MakeByRefType())      
        abstract member MakeArrayType: unit -> ProvidedType
        default __.MakeArrayType() = ProvidedType.CreateNoContext(x.MakeArrayType())        
        abstract member MakeArrayType: rank: int -> ProvidedType
        default __.MakeArrayType rank = ProvidedType.CreateNoContext(x.MakeArrayType(rank))        
        abstract member MakeGenericType: args: ProvidedType[] -> ProvidedType
        default __.MakeGenericType (args: ProvidedType[]) =
            let argTypes = args |> Array.map (fun arg -> arg.RawSystemType)
            ProvidedType.CreateNoContext(x.MakeGenericType(argTypes))
        static member Create ctxt x = match x with null -> null | t -> ProvidedType (t, ctxt)
        static member CreateWithNullCheck ctxt name x = match x with null -> nullArg name | t -> ProvidedType (t, ctxt)
        static member CreateArray ctxt xs = match xs with null -> null | _ -> xs |> Array.map (ProvidedType.Create ctxt)
        static member CreateNoContext (x: Type) = ProvidedType.Create ProvidedTypeContext.Empty x
        static member Void = ProvidedType.CreateNoContext typeof<Void>
        member __.Handle = x
        override __.Equals y = assert false; match y with :? ProvidedType as y -> x.Equals y.Handle | _ -> false
        override __.GetHashCode() = assert false; x.GetHashCode()
        abstract member Context: ProvidedTypeContext
        default __.Context = ctxt
        member this.TryGetILTypeRef() = this.Context.TryGetILTypeRef this
        member this.TryGetTyconRef() = this.Context.TryGetTyconRef this
        static member ApplyContext (pt: ProvidedType, ctxt) = ProvidedType(pt.Handle, ctxt)
        abstract member AsProvidedVar: nm: string -> ProvidedVar
        default __.AsProvidedVar (nm) = ProvidedVar.Create ctxt (Quotations.Var(nm, x))
        abstract member ApplyContext: ProvidedTypeContext -> ProvidedType
        default pt.ApplyContext (ctxt) = ProvidedType(pt.Handle, ctxt)
        static member TaintedEquals (pt1: Tainted<ProvidedType>, pt2: Tainted<ProvidedType>) =
           Tainted.PhysicallyEqTainted (pt1.PApplyNoFailure(fun st -> (st.Assembly.FullName, st.FullName))) (pt2.PApplyNoFailure(fun st -> (st.Assembly.FullName, st.FullName)))

    and [<AllowNullLiteral>] 
        IProvidedCustomAttributeProvider =
        abstract GetCustomAttributes : provider: ITypeProvider -> seq<CustomAttributeData>
        abstract GetDefinitionLocationAttribute : provider: ITypeProvider -> (string * int * int) option 
        abstract GetXmlDocAttributes : provider: ITypeProvider -> string[]
        abstract GetHasTypeProviderEditorHideMethodsAttribute : provider: ITypeProvider -> bool
        abstract GetAttributeConstructorArgs: provider: ITypeProvider * attribName: string -> (obj option list * (string * obj option) list) option

    and ProvidedCustomAttributeProvider =
        static member Create (attributes :ITypeProvider -> seq<CustomAttributeData>): IProvidedCustomAttributeProvider = 
            let (|Member|_|) (s: string) (x: CustomAttributeNamedArgument) = if x.MemberName = s then Some x.TypedValue else None
            let (|Arg|_|) (x: CustomAttributeTypedArgument) = match x.Value with null -> None | v -> Some v
            let findAttribByName tyFullName (a: CustomAttributeData) = (a.Constructor.DeclaringType.FullName = tyFullName)  
            let findAttrib (ty: Type) a = findAttribByName ty.FullName a
            { new IProvidedCustomAttributeProvider with
                  member __.GetCustomAttributes provider = attributes provider
                  member __.GetAttributeConstructorArgs (provider, attribName) = 
                      attributes provider 
                        |> Seq.tryFind (findAttribByName  attribName)  
                        |> Option.map (fun a -> 
                            let ctorArgs = 
                                a.ConstructorArguments 
                                |> Seq.toList 
                                |> List.map (function Arg null -> None | Arg obj -> Some obj | _ -> None)
                            let namedArgs = 
                                a.NamedArguments 
                                |> Seq.toList 
                                |> List.map (fun arg -> arg.MemberName, match arg.TypedValue with Arg null -> None | Arg obj -> Some obj | _ -> None)
                            ctorArgs, namedArgs)

                  member _.GetHasTypeProviderEditorHideMethodsAttribute provider = 
                      attributes provider 
                        |> Seq.exists (findAttrib typeof<TypeProviderEditorHideMethodsAttribute>) 

                  member _.GetDefinitionLocationAttribute provider = 
                      attributes provider 
                        |> Seq.tryFind (findAttrib  typeof<TypeProviderDefinitionLocationAttribute>)  
                        |> Option.map (fun a -> 
                               (defaultArg (a.NamedArguments |> Seq.tryPick (function Member "FilePath" (Arg (:? string as v)) -> Some v | _ -> None)) null, 
                                defaultArg (a.NamedArguments |> Seq.tryPick (function Member "Line" (Arg (:? int as v)) -> Some v | _ -> None)) 0, 
                                defaultArg (a.NamedArguments |> Seq.tryPick (function Member "Column" (Arg (:? int as v)) -> Some v | _ -> None)) 0))

                  member _.GetXmlDocAttributes provider = 
                      attributes provider 
                        |> Seq.choose (fun a -> 
                             if findAttrib  typeof<TypeProviderXmlDocAttribute> a then 
                                match a.ConstructorArguments |> Seq.toList with 
                                | [ Arg(:? string as s) ] -> Some s
                                | _ -> None
                             else 
                                None) 
                        |> Seq.toArray  }

    and [<AllowNullLiteral; AbstractClass>] 
        ProvidedMemberInfo (x: MemberInfo, ctxt) = 
        let provide () = ProvidedCustomAttributeProvider.Create (fun _provider -> x.CustomAttributes)
        abstract member Name: string
        default __.Name = x.Name
        /// DeclaringType can be null if MemberInfo belongs to Module, not to Type
        abstract member DeclaringType: ProvidedType
        default __.DeclaringType = ProvidedType.Create ctxt x.DeclaringType
        abstract GetCustomAttributes : provider: ITypeProvider -> seq<CustomAttributeData>
        default __.GetCustomAttributes provider = provide().GetCustomAttributes provider
        abstract GetHasTypeProviderEditorHideMethodsAttribute : provider: ITypeProvider -> bool
        default __.GetHasTypeProviderEditorHideMethodsAttribute provider = provide().GetHasTypeProviderEditorHideMethodsAttribute provider
        abstract GetDefinitionLocationAttribute : provider: ITypeProvider -> (string * int * int) option 
        default __.GetDefinitionLocationAttribute provider = provide().GetDefinitionLocationAttribute provider
        abstract GetXmlDocAttributes : provider: ITypeProvider -> string[]
        default __.GetXmlDocAttributes provider = provide().GetXmlDocAttributes provider
        abstract GetAttributeConstructorArgs: provider: ITypeProvider * attribName: string -> (obj option list * (string * obj option) list) option
        default __.GetAttributeConstructorArgs (provider, attribName) = provide().GetAttributeConstructorArgs (provider, attribName)
        interface IProvidedCustomAttributeProvider with
            member this.GetCustomAttributes provider = this.GetCustomAttributes provider
            member this.GetHasTypeProviderEditorHideMethodsAttribute provider = this.GetHasTypeProviderEditorHideMethodsAttribute provider
            member this.GetDefinitionLocationAttribute provider = this.GetDefinitionLocationAttribute provider
            member this.GetXmlDocAttributes provider = this.GetXmlDocAttributes provider
            member this.GetAttributeConstructorArgs (provider, attribName) = this.GetAttributeConstructorArgs (provider, attribName)

    and [<AllowNullLiteral>] 
        ProvidedParameterInfo (x: ParameterInfo, ctxt) = 
        let provide () = ProvidedCustomAttributeProvider.Create (fun _provider -> x.CustomAttributes)
        abstract member Name: string
        default __.Name = x.Name
        abstract member IsOut: bool
        default __.IsOut = x.IsOut
        abstract member IsIn: bool
        default __.IsIn = x.IsIn
        abstract member IsOptional: bool
        default __.IsOptional = x.IsOptional
        abstract member RawDefaultValue: obj
        default __.RawDefaultValue = x.RawDefaultValue
        abstract member HasDefaultValue: bool
        default __.HasDefaultValue = x.Attributes.HasFlag(ParameterAttributes.HasDefault)
        /// ParameterInfo.ParameterType cannot be null
        abstract member ParameterType: ProvidedType
        member __.Handle = x
        default __.ParameterType = ProvidedType.CreateWithNullCheck ctxt "ParameterType" x.ParameterType 
        static member Create ctxt x = match x with null -> null | t -> ProvidedParameterInfo (t, ctxt)
        static member CreateArray ctxt xs = match xs with null -> null | _ -> xs |> Array.map (ProvidedParameterInfo.Create ctxt)  // TODO null wrong?
        abstract GetCustomAttributes : provider: ITypeProvider -> seq<CustomAttributeData>
        default __.GetCustomAttributes provider = provide().GetCustomAttributes provider
        abstract GetHasTypeProviderEditorHideMethodsAttribute : provider: ITypeProvider -> bool
        default __.GetHasTypeProviderEditorHideMethodsAttribute provider = provide().GetHasTypeProviderEditorHideMethodsAttribute provider
        abstract GetDefinitionLocationAttribute : provider: ITypeProvider -> (string * int * int) option 
        default __.GetDefinitionLocationAttribute provider = provide().GetDefinitionLocationAttribute provider
        abstract GetXmlDocAttributes : provider: ITypeProvider -> string[]
        default __.GetXmlDocAttributes provider = provide().GetXmlDocAttributes provider
        abstract GetAttributeConstructorArgs: provider: ITypeProvider * attribName: string -> (obj option list * (string * obj option) list) option
        default __.GetAttributeConstructorArgs (provider, attribName) = provide().GetAttributeConstructorArgs (provider, attribName)
        interface IProvidedCustomAttributeProvider with
            member this.GetCustomAttributes provider = this.GetCustomAttributes provider
            member this.GetHasTypeProviderEditorHideMethodsAttribute provider = this.GetHasTypeProviderEditorHideMethodsAttribute provider
            member this.GetDefinitionLocationAttribute provider = this.GetDefinitionLocationAttribute provider
            member this.GetXmlDocAttributes provider = this.GetXmlDocAttributes provider
            member this.GetAttributeConstructorArgs (provider, attribName) = this.GetAttributeConstructorArgs (provider, attribName)

        override __.Equals y = assert false; match y with :? ProvidedParameterInfo as y -> x.Equals y.Handle | _ -> false
        override __.GetHashCode() = assert false; x.GetHashCode()

    and [<AllowNullLiteral>] 
        ProvidedAssembly (x: Assembly) =
        abstract member GetName : unit -> AssemblyName
        default __.GetName() = x.GetName()
        abstract member FullName : string
        default __.FullName = x.FullName
        abstract member GetManifestModuleContents : ITypeProvider -> byte[]
        default __.GetManifestModuleContents(provider: ITypeProvider) = provider.GetGeneratedAssemblyContents x
        static member Create x = match x with null -> null | t -> ProvidedAssembly (t)
        member __.Handle = x
        override __.Equals y = assert false; match y with :? ProvidedAssembly as y -> x.Equals y.Handle | _ -> false
        override __.GetHashCode() = assert false; x.GetHashCode()

    and [<AllowNullLiteral; AbstractClass>] 
        ProvidedMethodBase (x: MethodBase, ctxt) = 
        inherit ProvidedMemberInfo(x, ctxt)
        member __.Context = ctxt
        abstract member IsGenericMethod: bool
        default __.IsGenericMethod = x.IsGenericMethod
        abstract member IsStatic: bool
        default __.IsStatic = x.IsStatic
        abstract member IsFamily: bool
        default __.IsFamily = x.IsFamily
        abstract member IsFamilyOrAssembly: bool
        default __.IsFamilyOrAssembly = x.IsFamilyOrAssembly
        abstract member IsFamilyAndAssembly: bool
        default __.IsFamilyAndAssembly = x.IsFamilyAndAssembly
        abstract member IsVirtual: bool
        default __.IsVirtual = x.IsVirtual
        abstract member IsFinal: bool
        default __.IsFinal = x.IsFinal
        abstract member IsPublic: bool
        default __.IsPublic = x.IsPublic
        abstract member IsAbstract: bool
        default __.IsAbstract = x.IsAbstract
        abstract member IsHideBySig: bool
        default __.IsHideBySig = x.IsHideBySig
        abstract member IsConstructor: bool
        default __.IsConstructor = x.IsConstructor
        abstract member GetParameters: unit -> ProvidedParameterInfo[]
        default __.GetParameters() = x.GetParameters() |> ProvidedParameterInfo.CreateArray ctxt 
        abstract member GetGenericArguments: unit -> ProvidedType[]
        default __.GetGenericArguments() = x.GetGenericArguments() |> ProvidedType.CreateArray ctxt
        member __.Handle = x
        static member TaintedGetHashCode (x: Tainted<ProvidedMethodBase>) =            
           Tainted.GetHashCodeTainted (x.PApplyNoFailure(fun st -> (st.Name, st.DeclaringType.Assembly.FullName, st.DeclaringType.FullName))) 
        static member TaintedEquals (pt1: Tainted<ProvidedMethodBase>, pt2: Tainted<ProvidedMethodBase>) = 
           Tainted.PhysicallyEqTainted (pt1.PApplyNoFailure(fun st -> (st.Name, st.DeclaringType.Assembly.FullName, st.DeclaringType.FullName))) (pt2.PApplyNoFailure(fun st -> (st.Name, st.DeclaringType.Assembly.FullName, st.DeclaringType.FullName)))

        abstract GetStaticParametersForMethod: provider: ITypeProvider -> ProvidedParameterInfo[]
        default __.GetStaticParametersForMethod(provider: ITypeProvider) = 
            let bindingFlags = BindingFlags.Instance ||| BindingFlags.NonPublic ||| BindingFlags.Public 

            let staticParams = 
                match provider with 
                | :? ITypeProvider2 as itp2 -> 
                    itp2.GetStaticParametersForMethod x  
                | _ -> 
                    // To allow a type provider to depend only on FSharp.Core 4.3.0.0, it can alternatively
                    // implement an appropriate method called GetStaticParametersForMethod
                    let meth =
                        provider.GetType().GetMethod( "GetStaticParametersForMethod", bindingFlags, null,
                            [| typeof<MethodBase> |], null)  
                    if isNull meth then [| |] else
                    let paramsAsObj = 
                        try meth.Invoke(provider, bindingFlags ||| BindingFlags.InvokeMethod, null, [| box x |], null) 
                        with err -> raise (StripException (StripException err))
                    paramsAsObj :?> ParameterInfo[] 

            staticParams |> ProvidedParameterInfo.CreateArray ctxt


        abstract member ApplyStaticArgumentsForMethod : provider: ITypeProvider * fullNameAfterArguments: string * staticArgs: obj[] -> ProvidedMethodBase
        default __.ApplyStaticArgumentsForMethod(provider: ITypeProvider, fullNameAfterArguments: string, staticArgs: obj[]) = 
            let bindingFlags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.InvokeMethod

            let mb = 
                match provider with 
                | :? ITypeProvider2 as itp2 -> 
                    itp2.ApplyStaticArgumentsForMethod(x, fullNameAfterArguments, staticArgs)  
                | _ -> 

                    // To allow a type provider to depend only on FSharp.Core 4.3.0.0, it can alternatively implement a method called GetStaticParametersForMethod
                    let meth =
                        provider.GetType().GetMethod( "ApplyStaticArgumentsForMethod", bindingFlags, null,
                            [| typeof<MethodBase>; typeof<string>; typeof<obj[]> |], null)  

                    match meth with 
                    | null -> failwith (FSComp.SR.estApplyStaticArgumentsForMethodNotImplemented())
                    | _ -> 
                    let mbAsObj = 
                       try meth.Invoke(provider, bindingFlags ||| BindingFlags.InvokeMethod, null, [| box x; box fullNameAfterArguments; box staticArgs  |], null) 
                       with err -> raise (StripException (StripException err))

                    match mbAsObj with 
                    | :? MethodBase as mb -> mb
                    | _ -> failwith (FSComp.SR.estApplyStaticArgumentsForMethodNotImplemented())
            match mb with 
            | :? MethodInfo as mi -> (mi |> ProvidedMethodInfo.Create ctxt: ProvidedMethodInfo) :> ProvidedMethodBase
            | :? ConstructorInfo as ci -> (ci |> ProvidedConstructorInfo.Create ctxt: ProvidedConstructorInfo) :> ProvidedMethodBase
            | _ -> failwith (FSComp.SR.estApplyStaticArgumentsForMethodNotImplemented())


    and [<AllowNullLiteral>] 
        ProvidedFieldInfo (x: FieldInfo, ctxt) = 
        inherit ProvidedMemberInfo(x, ctxt)
        static member Create ctxt x = match x with null -> null | t -> ProvidedFieldInfo (t, ctxt)
        static member CreateArray ctxt xs = match xs with null -> null | _ -> xs |> Array.map (ProvidedFieldInfo.Create ctxt)
        abstract member IsInitOnly: bool
        default __.IsInitOnly = x.IsInitOnly
        abstract member IsStatic: bool
        default __.IsStatic = x.IsStatic
        abstract member IsSpecialName: bool
        default __.IsSpecialName = x.IsSpecialName
        abstract member IsLiteral: bool
        default __.IsLiteral = x.IsLiteral
        abstract member GetRawConstantValue: unit -> obj
        default __.GetRawConstantValue() = x.GetRawConstantValue()
        /// FieldInfo.FieldType cannot be null
        abstract member FieldType: ProvidedType
        default __.FieldType = x.FieldType |> ProvidedType.CreateWithNullCheck ctxt "FieldType" 
        member __.Handle = x
        abstract member IsPublic: bool
        default __.IsPublic = x.IsPublic
        abstract member IsFamily: bool
        default __.IsFamily = x.IsFamily
        abstract member IsPrivate: bool
        default __.IsPrivate = x.IsPrivate
        abstract member IsFamilyOrAssembly: bool
        default __.IsFamilyOrAssembly = x.IsFamilyOrAssembly
        abstract member IsFamilyAndAssembly: bool
        default __.IsFamilyAndAssembly = x.IsFamilyAndAssembly
        override __.Equals y = assert false; match y with :? ProvidedFieldInfo as y -> x.Equals y.Handle | _ -> false
        override __.GetHashCode() = assert false; x.GetHashCode()
        static member TaintedEquals (pt1: Tainted<ProvidedFieldInfo>, pt2: Tainted<ProvidedFieldInfo>) = 
           Tainted.PhysicallyEqTainted (pt1.PApplyNoFailure(fun st -> (st.Name, st.DeclaringType.Assembly.FullName, st.DeclaringType.FullName))) (pt2.PApplyNoFailure(fun st -> (st.Name, st.DeclaringType.Assembly.FullName, st.DeclaringType.FullName)))



    and [<AllowNullLiteral>] 
        ProvidedMethodInfo (x: MethodInfo, ctxt) = 
        inherit ProvidedMethodBase(x, ctxt)
        
        abstract member ReturnType: ProvidedType
        default __.ReturnType = x.ReturnType |> ProvidedType.CreateWithNullCheck ctxt "ReturnType"

        static member Create ctxt x = match x with null -> null | t -> ProvidedMethodInfo (t, ctxt)

        static member CreateArray ctxt xs = match xs with null -> null | _ -> xs |> Array.map (ProvidedMethodInfo.Create ctxt)
        member __.Handle = x
        abstract member MetadataToken: int
        default __.MetadataToken = x.MetadataToken
        override __.Equals y = assert false; match y with :? ProvidedMethodInfo as y -> x.Equals y.Handle | _ -> false
        override __.GetHashCode() = assert false; x.GetHashCode()

    and [<AllowNullLiteral>] 
        ProvidedPropertyInfo (x: PropertyInfo, ctxt) = 
        inherit ProvidedMemberInfo(x, ctxt)
        abstract member GetGetMethod: unit -> ProvidedMethodInfo
        default __.GetGetMethod() = x.GetGetMethod() |> ProvidedMethodInfo.Create ctxt
        abstract member GetSetMethod: unit -> ProvidedMethodInfo
        default __.GetSetMethod() = x.GetSetMethod() |> ProvidedMethodInfo.Create ctxt
        abstract member CanRead: bool
        default __.CanRead = x.CanRead
        abstract member CanWrite: bool
        default __.CanWrite = x.CanWrite
        abstract member GetIndexParameters: unit -> ProvidedParameterInfo[]
        default __.GetIndexParameters() = x.GetIndexParameters() |> ProvidedParameterInfo.CreateArray ctxt
        /// PropertyInfo.PropertyType cannot be null
        abstract member PropertyType: ProvidedType
        default __.PropertyType = x.PropertyType |> ProvidedType.CreateWithNullCheck ctxt "PropertyType"
        static member Create ctxt x = match x with null -> null | t -> ProvidedPropertyInfo (t, ctxt)
        static member CreateArray ctxt xs = match xs with null -> null | _ -> xs |> Array.map (ProvidedPropertyInfo.Create ctxt)
        member _.Handle = x
        override _.Equals y = assert false; match y with :? ProvidedPropertyInfo as y -> x.Equals y.Handle | _ -> false
        override _.GetHashCode() = assert false; x.GetHashCode()
        static member TaintedGetHashCode (x: Tainted<ProvidedPropertyInfo>) = 
           Tainted.GetHashCodeTainted (x.PApplyNoFailure(fun st -> (st.Name, st.DeclaringType.Assembly.FullName, st.DeclaringType.FullName))) 
        static member TaintedEquals (pt1: Tainted<ProvidedPropertyInfo>, pt2: Tainted<ProvidedPropertyInfo>) = 
           Tainted.PhysicallyEqTainted (pt1.PApplyNoFailure(fun st -> (st.Name, st.DeclaringType.Assembly.FullName, st.DeclaringType.FullName))) (pt2.PApplyNoFailure(fun st -> (st.Name, st.DeclaringType.Assembly.FullName, st.DeclaringType.FullName)))

    and [<AllowNullLiteral>] 
        ProvidedEventInfo (x: EventInfo, ctxt) = 
        inherit ProvidedMemberInfo(x, ctxt)
        abstract member GetAddMethod: unit -> ProvidedMethodInfo
        default __.GetAddMethod() = x.GetAddMethod() |> ProvidedMethodInfo.Create  ctxt
        abstract member GetRemoveMethod: unit -> ProvidedMethodInfo
        default __.GetRemoveMethod() = x.GetRemoveMethod() |> ProvidedMethodInfo.Create ctxt
        /// EventInfo.EventHandlerType cannot be null
        abstract member EventHandlerType: ProvidedType
        default __.EventHandlerType= x.EventHandlerType |> ProvidedType.CreateWithNullCheck ctxt "EventHandlerType"
        static member Create ctxt x = match x with null -> null | t -> ProvidedEventInfo (t, ctxt)
        static member CreateArray ctxt xs = match xs with null -> null | _ -> xs |> Array.map (ProvidedEventInfo.Create ctxt)
        member _.Handle = x
        override _.Equals y = assert false; match y with :? ProvidedEventInfo as y -> x.Equals y.Handle | _ -> false
        override _.GetHashCode() = assert false; x.GetHashCode()
        static member TaintedGetHashCode (x: Tainted<ProvidedEventInfo>) = 
           Tainted.GetHashCodeTainted (x.PApplyNoFailure(fun st -> (st.Name, st.DeclaringType.Assembly.FullName, st.DeclaringType.FullName))) 
        static member TaintedEquals (pt1: Tainted<ProvidedEventInfo>, pt2: Tainted<ProvidedEventInfo>) = 
           Tainted.PhysicallyEqTainted (pt1.PApplyNoFailure(fun st -> (st.Name, st.DeclaringType.Assembly.FullName, st.DeclaringType.FullName))) (pt2.PApplyNoFailure(fun st -> (st.Name, st.DeclaringType.Assembly.FullName, st.DeclaringType.FullName)))

    and [<AllowNullLiteral>] 
        ProvidedConstructorInfo (x: ConstructorInfo, ctxt) = 
        inherit ProvidedMethodBase(x, ctxt)
        static member Create ctxt x = match x with null -> null | t -> ProvidedConstructorInfo (t, ctxt)
        static member CreateArray ctxt xs = match xs with null -> null | _ -> xs |> Array.map (ProvidedConstructorInfo.Create ctxt)
        member _.Handle = x
        override _.Equals y = assert false; match y with :? ProvidedConstructorInfo as y -> x.Equals y.Handle | _ -> false
        override _.GetHashCode() = assert false; x.GetHashCode()

    and ProvidedExprType =
        | ProvidedNewArrayExpr of ProvidedType * ProvidedExpr[]
#if PROVIDED_ADDRESS_OF
        | ProvidedAddressOfExpr of ProvidedExpr
#endif
        | ProvidedNewObjectExpr of ProvidedConstructorInfo * ProvidedExpr[]
        | ProvidedWhileLoopExpr of ProvidedExpr * ProvidedExpr
        | ProvidedNewDelegateExpr of ProvidedType * ProvidedVar[] * ProvidedExpr
        | ProvidedForIntegerRangeLoopExpr of ProvidedVar * ProvidedExpr * ProvidedExpr * ProvidedExpr
        | ProvidedSequentialExpr of ProvidedExpr * ProvidedExpr
        | ProvidedTryWithExpr of ProvidedExpr * ProvidedVar * ProvidedExpr * ProvidedVar * ProvidedExpr
        | ProvidedTryFinallyExpr of ProvidedExpr * ProvidedExpr
        | ProvidedLambdaExpr of ProvidedVar * ProvidedExpr
        | ProvidedCallExpr of ProvidedExpr option * ProvidedMethodInfo * ProvidedExpr[] 
        | ProvidedConstantExpr of obj * ProvidedType
        | ProvidedDefaultExpr of ProvidedType
        | ProvidedNewTupleExpr of ProvidedExpr[]
        | ProvidedTupleGetExpr of ProvidedExpr * int
        | ProvidedTypeAsExpr of ProvidedExpr * ProvidedType
        | ProvidedTypeTestExpr of ProvidedExpr * ProvidedType
        | ProvidedLetExpr of ProvidedVar * ProvidedExpr * ProvidedExpr
        | ProvidedVarSetExpr of ProvidedVar * ProvidedExpr
        | ProvidedIfThenElseExpr of ProvidedExpr * ProvidedExpr * ProvidedExpr
        | ProvidedVarExpr of ProvidedVar

    and [<RequireQualifiedAccess; Class; AllowNullLiteral>]
        ProvidedExpr (x: Quotations.Expr, ctxt) =
        abstract member Type: ProvidedType
        default __.Type = x.Type |> ProvidedType.Create ctxt
        member __.Context = ctxt
        abstract member UnderlyingExpressionString: string
        default __.UnderlyingExpressionString = x.ToString()
        abstract member GetExprType: unit -> ProvidedExprType option
        default __.GetExprType() =
            match x with
            | Quotations.Patterns.NewObject(ctor, args) ->
                Some (ProvidedNewObjectExpr (ProvidedConstructorInfo.Create ctxt ctor, [| for a in args -> ProvidedExpr.Create ctxt a |]))
            | Quotations.Patterns.WhileLoop(guardExpr, bodyExpr) ->
                Some (ProvidedWhileLoopExpr (ProvidedExpr.Create ctxt guardExpr, ProvidedExpr.Create ctxt bodyExpr))
            | Quotations.Patterns.NewDelegate(ty, vs, expr) ->
                Some (ProvidedNewDelegateExpr(ProvidedType.Create ctxt ty, ProvidedVar.CreateArray ctxt (List.toArray vs), ProvidedExpr.Create ctxt expr))
            | Quotations.Patterns.Call(objOpt, meth, args) ->
                Some (ProvidedCallExpr((match objOpt with None -> None | Some obj -> Some (ProvidedExpr.Create ctxt obj)), 
                        ProvidedMethodInfo.Create ctxt meth, [| for a in args -> ProvidedExpr.Create ctxt a |]))
            | Quotations.Patterns.DefaultValue ty ->
                Some (ProvidedDefaultExpr (ProvidedType.Create ctxt ty))
            | Quotations.Patterns.Value(obj, ty) ->
                Some (ProvidedConstantExpr (obj, ProvidedType.Create ctxt ty))
            | Quotations.Patterns.Coerce(arg, ty) ->
                Some (ProvidedTypeAsExpr (ProvidedExpr.Create ctxt arg, ProvidedType.Create ctxt ty))
            | Quotations.Patterns.NewTuple args ->
                Some (ProvidedNewTupleExpr(ProvidedExpr.CreateArray ctxt (Array.ofList args)))
            | Quotations.Patterns.TupleGet(arg, n) ->
                Some (ProvidedTupleGetExpr (ProvidedExpr.Create ctxt arg, n))
            | Quotations.Patterns.NewArray(ty, args) ->
                Some (ProvidedNewArrayExpr(ProvidedType.Create ctxt ty, ProvidedExpr.CreateArray ctxt (Array.ofList args)))
            | Quotations.Patterns.Sequential(e1, e2) ->
                Some (ProvidedSequentialExpr(ProvidedExpr.Create ctxt e1, ProvidedExpr.Create ctxt e2))
            | Quotations.Patterns.Lambda(v, body) ->
                Some (ProvidedLambdaExpr (ProvidedVar.Create ctxt v,  ProvidedExpr.Create ctxt body))
            | Quotations.Patterns.TryFinally(b1, b2) ->
                Some (ProvidedTryFinallyExpr (ProvidedExpr.Create ctxt b1, ProvidedExpr.Create ctxt b2))
            | Quotations.Patterns.TryWith(b, v1, e1, v2, e2) ->
                Some (ProvidedTryWithExpr (ProvidedExpr.Create ctxt b, ProvidedVar.Create ctxt v1, ProvidedExpr.Create ctxt e1, ProvidedVar.Create ctxt v2, ProvidedExpr.Create ctxt e2))
#if PROVIDED_ADDRESS_OF
            | Quotations.Patterns.AddressOf e -> Some (ProvidedAddressOfExpr (ProvidedExpr.Create ctxt e))
#endif
            | Quotations.Patterns.TypeTest(e, ty) ->
                Some (ProvidedTypeTestExpr(ProvidedExpr.Create ctxt e, ProvidedType.Create ctxt ty))
            | Quotations.Patterns.Let(v, e, b) ->
                Some (ProvidedLetExpr (ProvidedVar.Create ctxt v, ProvidedExpr.Create ctxt e, ProvidedExpr.Create ctxt b))
            | Quotations.Patterns.ForIntegerRangeLoop (v, e1, e2, e3) ->
                Some (ProvidedForIntegerRangeLoopExpr (ProvidedVar.Create ctxt v, ProvidedExpr.Create ctxt e1, ProvidedExpr.Create ctxt e2, ProvidedExpr.Create ctxt e3))
            | Quotations.Patterns.VarSet(v, e) ->
                Some (ProvidedVarSetExpr (ProvidedVar.Create ctxt v, ProvidedExpr.Create ctxt e))
            | Quotations.Patterns.IfThenElse(g, t, e) ->
                Some (ProvidedIfThenElseExpr (ProvidedExpr.Create ctxt g, ProvidedExpr.Create ctxt t, ProvidedExpr.Create ctxt e))
            | Quotations.Patterns.Var v ->
                Some (ProvidedVarExpr (ProvidedVar.Create ctxt v))
            | _ -> None
        member __.Handle = x
        static member Create ctxt t = match box t with null -> null | _ -> ProvidedExpr (t, ctxt)
        static member CreateArray ctxt xs = match xs with null -> null | _ -> xs |> Array.map (ProvidedExpr.Create ctxt)
        override _.Equals y = match y with :? ProvidedExpr as y -> x.Equals y.Handle | _ -> false
        override _.GetHashCode() = x.GetHashCode()

    and [<RequireQualifiedAccess; Class; AllowNullLiteral>]
        ProvidedVar (x: Quotations.Var, ctxt) =
        abstract member Type: ProvidedType
        default __.Type = x.Type |> ProvidedType.Create ctxt
        abstract member Name: string
        default __.Name = x.Name
        abstract member IsMutable: bool
        default __.IsMutable = x.IsMutable
        member __.Context = ctxt
        member __.Handle = x
        static member Create ctxt t = match box t with null -> null | _ -> ProvidedVar (t, ctxt)
        static member CreateArray ctxt xs = match xs with null -> null | _ -> xs |> Array.map (ProvidedVar.Create ctxt)
        override this.Equals y =
            match y with
            | :? ProvidedVar as y -> this.Name.Equals y.Name && this.IsMutable = y.IsMutable &&
                                     (ProvidedTypeComparer.Instance :> IEqualityComparer<_>).Equals(this.Type, y.Type)
            | _ -> false

        override this.GetHashCode() = this.Name.GetHashCode()
        
    [<AutoOpen>]
    module Shim =
        
        type IExtensionTypingProvider =
            abstract InstantiateTypeProvidersOfAssembly: 
              runtimeAssemblyFilename: string
              * designerAssemblyName: string 
              * resolutionEnvironment: ResolutionEnvironment
              * isInvalidationSupported: bool
              * isInteractive: bool
              * systemRuntimeContainsType: (string -> bool)
              * systemRuntimeAssemblyVersion: System.Version
              * compilerToolsPath: string list
              * logError: (TypeProviderError -> unit)
              * m: range -> ITypeProvider list
              
            abstract GetProvidedTypes: pn: IProvidedNamespace -> ProvidedType[]           
            abstract ResolveTypeName: pn: IProvidedNamespace * typeName: string -> ProvidedType             
            abstract GetInvokerExpression: provider: ITypeProvider * methodBase: ProvidedMethodBase * paramExprs: ProvidedVar[] -> ProvidedExpr
            abstract DisplayNameOfTypeProvider: typeProvider: ITypeProvider * fullName: bool -> string

        [<Sealed>]
        type DefaultExtensionTypingProvider() =
            interface IExtensionTypingProvider with
                member this.InstantiateTypeProvidersOfAssembly
                    (runTimeAssemblyFileName: string,
                     designTimeAssemblyNameString: string, 
                     resolutionEnvironment: ResolutionEnvironment, 
                     isInvalidationSupported: bool, 
                     isInteractive: bool, 
                     systemRuntimeContainsType: string -> bool, 
                     systemRuntimeAssemblyVersion: System.Version, 
                     compilerToolPaths: string list,
                     logError: TypeProviderError -> unit,
                     m: range) =

                    GetTypeProvidersOfAssemblyInternal
                        (runTimeAssemblyFileName,
                         designTimeAssemblyNameString,
                         resolutionEnvironment,
                         isInvalidationSupported,
                         isInteractive,
                         systemRuntimeContainsType,
                         systemRuntimeAssemblyVersion,
                         compilerToolPaths,
                         logError,
                         m)

                member this.GetProvidedTypes(pn: IProvidedNamespace) =
                    pn.GetTypes() |> Array.map ProvidedType.CreateNoContext 
            
                member this.ResolveTypeName(pn: IProvidedNamespace, typeName: string) =
                    pn.ResolveTypeName typeName |> ProvidedType.CreateNoContext
         
                member this.GetInvokerExpression(provider: ITypeProvider, methodBase: ProvidedMethodBase, paramExprs: ProvidedVar[]) =
                    provider.GetInvokerExpression(methodBase.Handle, [| for p in paramExprs -> Quotations.Expr.Var (p.Handle) |]) |> ProvidedExpr.Create methodBase.Context
                    
                member this.DisplayNameOfTypeProvider(tp: ITypeProvider, fullName: bool) =
                    if fullName then tp.GetType().FullName else tp.GetType().Name

        let mutable ExtensionTypingProvider = DefaultExtensionTypingProvider() :> IExtensionTypingProvider

        let shimLogger (tpe: TypeProviderError) =
            tpe.Iter(fun e -> errorR(Error((e.Number, e.ContextualErrorMessage), e.Range)))


    let GetTypeProvidersOfAssembly
        (runTimeAssemblyFileName: string, 
         ilScopeRefOfRuntimeAssembly: ILScopeRef, 
         designTimeAssemblyNameString: string, 
         resolutionEnvironment: ResolutionEnvironment, 
         isInvalidationSupported: bool, 
         isInteractive: bool, 
         systemRuntimeContainsType : string -> bool, 
         systemRuntimeAssemblyVersion : System.Version, 
         compilerToolPaths: string list,
         m: range) =
    
        let providers = ExtensionTypingProvider.InstantiateTypeProvidersOfAssembly(
                         runTimeAssemblyFileName,
                         designTimeAssemblyNameString,
                         resolutionEnvironment,
                         isInvalidationSupported,
                         isInteractive,
                         systemRuntimeContainsType,
                         systemRuntimeAssemblyVersion,
                         compilerToolPaths,
                         Shim.shimLogger,
                         m)
              
        Tainted<_>.CreateAll (providers |> List.map (fun p -> p, ilScopeRefOfRuntimeAssembly, ExtensionTypingProvider.DisplayNameOfTypeProvider(p, true)))

    /// Get the provided invoker expression for a particular use of a method.
    let GetInvokerExpression (provider: ITypeProvider, methodBase: ProvidedMethodBase, paramExprs: ProvidedVar[]) = 
        ExtensionTypingProvider.GetInvokerExpression(provider, methodBase, paramExprs)
        
    /// Get all provided types from provided namespace
    let GetProvidedTypes (pn: IProvidedNamespace) = 
        ExtensionTypingProvider.GetProvidedTypes(pn)
        
    // Get the string to show for the name of a type provider
    let DisplayNameOfTypeProvider (resolver: Tainted<ITypeProvider>, m: range) =
        resolver.PUntaint((fun tp -> ExtensionTypingProvider.DisplayNameOfTypeProvider(tp, false)), m)

    /// Compute the Name or FullName property of a provided type, reporting appropriate errors
    let CheckAndComputeProvidedNameProperty(m, st: Tainted<ProvidedType>, proj, propertyString) =
        let name = 
            try st.PUntaint(proj, m) 
            with :? TypeProviderError as tpe -> 
                let newError = tpe.MapText((fun msg -> FSComp.SR.etProvidedTypeWithNameException(propertyString, msg)), st.TypeProviderDesignation, m)
                raise newError
        if String.IsNullOrEmpty name then
            raise (TypeProviderError(FSComp.SR.etProvidedTypeWithNullOrEmptyName propertyString, st.TypeProviderDesignation, m))
        name

    /// Verify that this type provider has supported attributes
    let ValidateAttributesOfProvidedType (m, st: Tainted<ProvidedType>) =         
        let fullName = CheckAndComputeProvidedNameProperty(m, st, (fun st -> st.FullName), "FullName")
        if TryTypeMember(st, fullName, "IsGenericType", m, false, fun st->st.IsGenericType) |> unmarshal then  
            errorR(Error(FSComp.SR.etMustNotBeGeneric fullName, m))  
        if TryTypeMember(st, fullName, "IsArray", m, false, fun st->st.IsArray) |> unmarshal then 
            errorR(Error(FSComp.SR.etMustNotBeAnArray fullName, m))  
        TryTypeMemberNonNull(st, fullName, "GetInterfaces", m, [||], fun st -> st.GetInterfaces()) |> ignore


    /// Verify that a provided type has the expected name
    let ValidateExpectedName m expectedPath expectedName (st: Tainted<ProvidedType>) =
        let name = CheckAndComputeProvidedNameProperty(m, st, (fun st -> st.Name), "Name")
        if name <> expectedName then
            raise (TypeProviderError(FSComp.SR.etProvidedTypeHasUnexpectedName(expectedName, name), st.TypeProviderDesignation, m))

        let namespaceName = TryTypeMember(st, name, "Namespace", m, "", fun st -> st.Namespace) |> unmarshal
        let rec declaringTypes (st: Tainted<ProvidedType>) accu =
            match TryTypeMember(st, name, "DeclaringType", m, null, fun st -> st.DeclaringType) with
            |   Tainted.Null -> accu
            |   dt -> declaringTypes dt (CheckAndComputeProvidedNameProperty(m, dt, (fun dt -> dt.Name), "Name") :: accu)
        let path = 
            [|  match namespaceName with 
                | null -> ()
                | _ -> yield! namespaceName.Split([|'.'|])
                yield! declaringTypes st [] |]
        
        if path <> expectedPath then
            let expectedPath = String.Join(".", expectedPath)
            let path = String.Join(".", path)
            errorR(Error(FSComp.SR.etProvidedTypeHasUnexpectedPath(expectedPath, path), m))

    /// Eagerly validate a range of conditions on a provided type, after static instantiation (if any) has occurred
    let ValidateProvidedTypeAfterStaticInstantiation(m, st: Tainted<ProvidedType>, expectedPath: string[], expectedName: string) = 
        // Do all the calling into st up front with recovery
        let fullName, namespaceName, usedMembers =
            let name = CheckAndComputeProvidedNameProperty(m, st, (fun st -> st.Name), "Name")
            let namespaceName = TryTypeMember(st, name, "Namespace", m, FSComp.SR.invalidNamespaceForProvidedType(), fun st -> st.Namespace) |> unmarshal
            let fullName = TryTypeMemberNonNull(st, name, "FullName", m, FSComp.SR.invalidFullNameForProvidedType(), fun st -> st.FullName) |> unmarshal
            ValidateExpectedName m expectedPath expectedName st
            // Must be able to call (GetMethods|GetEvents|GetProperties|GetNestedTypes|GetConstructors)(bindingFlags).
            let usedMembers: Tainted<ProvidedMemberInfo>[] = 
                // These are the members the compiler will actually use
                [| for x in TryTypeMemberArray(st, fullName, "GetMethods", m, fun st -> st.GetMethods()) -> x.Coerce m
                   for x in TryTypeMemberArray(st, fullName, "GetEvents", m, fun st -> st.GetEvents()) -> x.Coerce m
                   for x in TryTypeMemberArray(st, fullName, "GetFields", m, fun st -> st.GetFields()) -> x.Coerce m
                   for x in TryTypeMemberArray(st, fullName, "GetProperties", m, fun st -> st.GetProperties()) -> x.Coerce m
                   // These will be validated on-demand
                   //for x in TryTypeMemberArray(st, fullName, "GetNestedTypes", m, fun st -> st.GetNestedTypes bindingFlags) -> x.Coerce()
                   for x in TryTypeMemberArray(st, fullName, "GetConstructors", m, fun st -> st.GetConstructors()) -> x.Coerce m |]
            fullName, namespaceName, usedMembers       

        // We scrutinize namespaces for invalid characters on open, but this provides better diagnostics
        ValidateNamespaceName(fullName, st.TypeProvider, m, namespaceName)

        ValidateAttributesOfProvidedType(m, st)

        // Those members must have this type.
        // This needs to be a *shallow* exploration. Otherwise, as in Freebase sample the entire database could be explored.
        for mi in usedMembers do
            match mi with 
            | Tainted.Null -> errorR(Error(FSComp.SR.etNullMember fullName, m))  
            | _ -> 
                let memberName = TryMemberMember(mi, fullName, "Name", "Name", m, "invalid provided type member name", fun mi -> mi.Name) |> unmarshal
                if String.IsNullOrEmpty memberName then 
                    errorR(Error(FSComp.SR.etNullOrEmptyMemberName fullName, m))  
                else 
                    let miDeclaringType = TryMemberMember(mi, fullName, memberName, "DeclaringType", m, ProvidedType.CreateNoContext(typeof<obj>), fun mi -> mi.DeclaringType)
                    match miDeclaringType with 
                        // Generated nested types may have null DeclaringType
                    | Tainted.Null when mi.OfType<ProvidedType>().IsSome -> ()
                    | Tainted.Null -> 
                        errorR(Error(FSComp.SR.etNullMemberDeclaringType(fullName, memberName), m))   
                    | _ ->     
                        let miDeclaringTypeFullName = 
                            TryMemberMember (miDeclaringType, fullName, memberName, "FullName", m,
                                "invalid declaring type full name",
                                fun miDeclaringType -> miDeclaringType.FullName)
                            |> unmarshal

                        if not (ProvidedType.TaintedEquals (st, miDeclaringType)) then 
                            errorR(Error(FSComp.SR.etNullMemberDeclaringTypeDifferentFromProvidedType(fullName, memberName, miDeclaringTypeFullName), m))   

                    match mi.OfType<ProvidedMethodInfo>() with
                    | Some mi ->
                        let isPublic = TryMemberMember(mi, fullName, memberName, "IsPublic", m, true, fun mi->mi.IsPublic) |> unmarshal
                        let isGenericMethod = TryMemberMember(mi, fullName, memberName, "IsGenericMethod", m, true, fun mi->mi.IsGenericMethod) |> unmarshal
                        if not isPublic || isGenericMethod then
                            errorR(Error(FSComp.SR.etMethodHasRequirements(fullName, memberName), m))   
                    |   None ->
                    match mi.OfType<ProvidedType>() with
                    |   Some subType -> ValidateAttributesOfProvidedType(m, subType)
                    |   None ->
                    match mi.OfType<ProvidedPropertyInfo>() with
                    | Some pi ->
                        // Property must have a getter or setter
                        // TODO: Property must be public etc.
                        let expectRead =
                             match TryMemberMember(pi, fullName, memberName, "GetGetMethod", m, null, fun pi -> pi.GetGetMethod()) with 
                             |  Tainted.Null -> false 
                             | _ -> true
                        let expectWrite = 
                            match TryMemberMember(pi, fullName, memberName, "GetSetMethod", m, null, fun pi-> pi.GetSetMethod()) with 
                            |   Tainted.Null -> false 
                            |   _ -> true
                        let canRead = TryMemberMember(pi, fullName, memberName, "CanRead", m, expectRead, fun pi-> pi.CanRead) |> unmarshal
                        let canWrite = TryMemberMember(pi, fullName, memberName, "CanWrite", m, expectWrite, fun pi-> pi.CanWrite) |> unmarshal
                        match expectRead, canRead with
                        | false, false | true, true-> ()
                        | false, true -> errorR(Error(FSComp.SR.etPropertyCanReadButHasNoGetter(memberName, fullName), m))   
                        | true, false -> errorR(Error(FSComp.SR.etPropertyHasGetterButNoCanRead(memberName, fullName), m))   
                        match expectWrite, canWrite with
                        | false, false | true, true-> ()
                        | false, true -> errorR(Error(FSComp.SR.etPropertyCanWriteButHasNoSetter(memberName, fullName), m))   
                        | true, false -> errorR(Error(FSComp.SR.etPropertyHasSetterButNoCanWrite(memberName, fullName), m))   
                        if not canRead && not canWrite then 
                            errorR(Error(FSComp.SR.etPropertyNeedsCanWriteOrCanRead(memberName, fullName), m))   

                    | None ->
                    match mi.OfType<ProvidedEventInfo>() with 
                    | Some ei ->
                        // Event must have adder and remover
                        // TODO: Event must be public etc.
                        let adder = TryMemberMember(ei, fullName, memberName, "GetAddMethod", m, null, fun ei-> ei.GetAddMethod())
                        let remover = TryMemberMember(ei, fullName, memberName, "GetRemoveMethod", m, null, fun ei-> ei.GetRemoveMethod())
                        match adder, remover with
                        | Tainted.Null, _ -> errorR(Error(FSComp.SR.etEventNoAdd(memberName, fullName), m))   
                        | _, Tainted.Null -> errorR(Error(FSComp.SR.etEventNoRemove(memberName, fullName), m))   
                        | _, _ -> ()
                    | None ->
                    match mi.OfType<ProvidedConstructorInfo>() with
                    | Some _  -> () // TODO: Constructors must be public etc.
                    | None ->
                    match mi.OfType<ProvidedFieldInfo>() with
                    | Some _ -> () // TODO: Fields must be public, literals must have a value etc.
                    | None ->
                        errorR(Error(FSComp.SR.etUnsupportedMemberKind(memberName, fullName), m))   

    let ValidateProvidedTypeDefinition(m, st: Tainted<ProvidedType>, expectedPath: string[], expectedName: string) = 

        // Validate the Name, Namespace and FullName properties
        let name = CheckAndComputeProvidedNameProperty(m, st, (fun st -> st.Name), "Name")
        let _namespaceName = TryTypeMember(st, name, "Namespace", m, FSComp.SR.invalidNamespaceForProvidedType(), fun st -> st.Namespace) |> unmarshal
        let _fullname = TryTypeMemberNonNull(st, name, "FullName", m, FSComp.SR.invalidFullNameForProvidedType(), fun st -> st.FullName)  |> unmarshal
        ValidateExpectedName m expectedPath expectedName st

        ValidateAttributesOfProvidedType(m, st)

        // This excludes, for example, types with '.' in them which would not be resolvable during name resolution.
        match expectedName.IndexOfAny(PrettyNaming.IllegalCharactersInTypeAndNamespaceNames) with
        | -1 -> ()
        | n -> errorR(Error(FSComp.SR.etIllegalCharactersInTypeName(string expectedName.[n], expectedName), m))  

        let staticParameters = st.PApplyWithProvider((fun (st, provider) -> st.GetStaticParameters provider), range=m) 
        if staticParameters.PUntaint((fun a -> a.Length), m)  = 0 then 
            ValidateProvidedTypeAfterStaticInstantiation(m, st, expectedPath, expectedName)


    /// Resolve a (non-nested) provided type given a full namespace name and a type name. 
    /// May throw an exception which will be turned into an error message by one of the 'Try' function below.
    /// If resolution is successful the type is then validated.
    let ResolveProvidedType (resolver: Tainted<ITypeProvider>, m, moduleOrNamespace: string[], typeName) =
        let displayName = String.Join(".", moduleOrNamespace)

        // Try to find the type in the given provided namespace
        let rec tryNamespace (providedNamespace: Tainted<IProvidedNamespace>) = 

            // Get the provided namespace name
            let providedNamespaceName = providedNamespace.PUntaint((fun providedNamespace -> providedNamespace.NamespaceName), range=m)

            // Check if the provided namespace name is an exact match of the required namespace name
            if displayName = providedNamespaceName then
                let resolvedType = providedNamespace.PApply((fun providedNamespace -> ExtensionTypingProvider.ResolveTypeName(providedNamespace, typeName)), range=m) 
                match resolvedType with
                |   Tainted.Null -> None
                |   result -> 
                    ValidateProvidedTypeDefinition(m, result, moduleOrNamespace, typeName)
                    Some result
            else
                // Note: This eagerly explores all provided namespaces even if there is no match of even a prefix in the
                // namespace names. 
                let providedNamespaces = providedNamespace.PApplyArray((fun providedNamespace -> providedNamespace.GetNestedNamespaces()), "GetNestedNamespaces", range=m)
                tryNamespaces providedNamespaces

        and tryNamespaces (providedNamespaces: Tainted<IProvidedNamespace>[]) = 
            providedNamespaces |> Array.tryPick tryNamespace

        let providedNamespaces = resolver.PApplyArray((fun resolver -> resolver.GetNamespaces()), "GetNamespaces", range=m)
        match tryNamespaces providedNamespaces with 
        | None -> resolver.PApply((fun _ -> null), m)
        | Some res -> res
                    
    /// Try to resolve a type against the given host with the given resolution environment.
    let TryResolveProvidedType(resolver: Tainted<ITypeProvider>, m, moduleOrNamespace, typeName) =
        try 
            match ResolveProvidedType(resolver, m, moduleOrNamespace, typeName) with
            | Tainted.Null -> None
            | ty -> Some ty
        with e -> 
            errorRecovery e m
            None

    let ILPathToProvidedType  (st: Tainted<ProvidedType>, m) = 
        let nameContrib (st: Tainted<ProvidedType>) = 
            let typeName = st.PUntaint((fun st -> st.Name), m)
            match st.PApply((fun st -> st.DeclaringType), m) with 
            | Tainted.Null -> 
               match st.PUntaint((fun st -> st.Namespace), m) with 
               | null -> typeName
               | ns -> ns + "." + typeName
            | _ -> typeName

        let rec encContrib (st: Tainted<ProvidedType>) = 
            match st.PApply((fun st ->st.DeclaringType), m) with 
            | Tainted.Null -> []
            | enc -> encContrib enc @ [ nameContrib enc ]

        encContrib st, nameContrib st

    let ComputeMangledNameForApplyStaticParameters(nm, staticArgs, staticParams: Tainted<ProvidedParameterInfo[]>, m) =
        let defaultArgValues = 
            staticParams.PApply((fun ps ->  ps |> Array.map (fun sp -> sp.Name, (if sp.IsOptional then Some (string sp.RawDefaultValue) else None ))), range=m)

        let defaultArgValues = defaultArgValues.PUntaint(id, m)
        PrettyNaming.computeMangledNameWithoutDefaultArgValues(nm, staticArgs, defaultArgValues)

    /// Apply the given provided method to the given static arguments (the arguments are assumed to have been sorted into application order)
    let TryApplyProvidedMethod(methBeforeArgs: Tainted<ProvidedMethodBase>, staticArgs: obj[], m: range) =
        if staticArgs.Length = 0 then 
            Some methBeforeArgs
        else
            let mangledName = 
                let nm = methBeforeArgs.PUntaint((fun x -> x.Name), m)
                let staticParams = methBeforeArgs.PApplyWithProvider((fun (mb, resolver) -> mb.GetStaticParametersForMethod resolver), range=m) 
                let mangledName = ComputeMangledNameForApplyStaticParameters(nm, staticArgs, staticParams, m)
                mangledName
 
            match methBeforeArgs.PApplyWithProvider((fun (mb, provider) -> mb.ApplyStaticArgumentsForMethod(provider, mangledName, staticArgs)), range=m) with 
            | Tainted.Null -> None
            | methWithArguments -> 
                let actualName = methWithArguments.PUntaint((fun x -> x.Name), m)
                if actualName <> mangledName then 
                    error(Error(FSComp.SR.etProvidedAppliedMethodHadWrongName(methWithArguments.TypeProviderDesignation, mangledName, actualName), m))
                Some methWithArguments


    /// Apply the given provided type to the given static arguments (the arguments are assumed to have been sorted into application order
    let TryApplyProvidedType(typeBeforeArguments: Tainted<ProvidedType>, optGeneratedTypePath: string list option, staticArgs: obj[], m: range) =
        if staticArgs.Length = 0 then 
            Some (typeBeforeArguments, (fun () -> ()))
        else 
            
            let fullTypePathAfterArguments = 
                // If there is a generated type name, then use that
                match optGeneratedTypePath with 
                | Some path -> path
                | None -> 
                    // Otherwise, use the full path of the erased type, including mangled arguments
                    let nm = typeBeforeArguments.PUntaint((fun x -> x.Name), m)
                    let enc, _ = ILPathToProvidedType (typeBeforeArguments, m)
                    let staticParams = typeBeforeArguments.PApplyWithProvider((fun (mb, resolver) -> mb.GetStaticParameters resolver), range=m) 
                    let mangledName = ComputeMangledNameForApplyStaticParameters(nm, staticArgs, staticParams, m)
                    enc @ [ mangledName ]
 
            match typeBeforeArguments.PApplyWithProvider((fun (typeBeforeArguments, provider) -> typeBeforeArguments.ApplyStaticArguments(provider, Array.ofList fullTypePathAfterArguments, staticArgs)), range=m) with 
            | Tainted.Null -> None
            | typeWithArguments -> 
                let actualName = typeWithArguments.PUntaint((fun x -> x.Name), m)
                let checkTypeName() = 
                    let expectedTypeNameAfterArguments = fullTypePathAfterArguments.[fullTypePathAfterArguments.Length-1]
                    if actualName <> expectedTypeNameAfterArguments then 
                        error(Error(FSComp.SR.etProvidedAppliedTypeHadWrongName(typeWithArguments.TypeProviderDesignation, expectedTypeNameAfterArguments, actualName), m))
                Some (typeWithArguments, checkTypeName)

    /// Given a mangled name reference to a non-nested provided type, resolve it.
    /// If necessary, demangle its static arguments before applying them.
    let TryLinkProvidedType(resolver: Tainted<ITypeProvider>, moduleOrNamespace: string[], typeLogicalName: string, range: range) =
        
        // Demangle the static parameters
        let typeName, argNamesAndValues = 
            try 
                PrettyNaming.demangleProvidedTypeName typeLogicalName 
            with PrettyNaming.InvalidMangledStaticArg piece -> 
                error(Error(FSComp.SR.etProvidedTypeReferenceInvalidText piece, range0)) 

        let argSpecsTable = dict argNamesAndValues
        let typeBeforeArguments = ResolveProvidedType(resolver, range0, moduleOrNamespace, typeName) 

        match typeBeforeArguments with 
        | Tainted.Null -> None
        | _ -> 
            // Take the static arguments (as strings, taken from the text in the reference we're relinking), 
            // and convert them to objects of the appropriate type, based on the expected kind.
            let staticParameters =
                typeBeforeArguments.PApplyWithProvider((fun (typeBeforeArguments, resolver) ->
                    typeBeforeArguments.GetStaticParameters resolver),range=range0)

            let staticParameters = staticParameters.PApplyArray(id, "", range)
            
            let staticArgs = 
                staticParameters |> Array.map (fun sp -> 
                      let typeBeforeArgumentsName = typeBeforeArguments.PUntaint ((fun st -> st.Name), range)
                      let spName = sp.PUntaint ((fun sp -> sp.Name), range)
                      match argSpecsTable.TryGetValue spName with
                      | true, arg ->
                          /// Find the name of the representation type for the static parameter
                          let spReprTypeName = 
                              sp.PUntaint((fun sp -> 
                                  let pt = sp.ParameterType
                                  let uet = if pt.IsEnum then pt.GetEnumUnderlyingType() else pt
                                  uet.FullName), range)

                          match spReprTypeName with 
                          | "System.SByte" -> box (sbyte arg)
                          | "System.Int16" -> box (int16 arg)
                          | "System.Int32" -> box (int32 arg)
                          | "System.Int64" -> box (int64 arg)
                          | "System.Byte" -> box (byte arg)
                          | "System.UInt16" -> box (uint16 arg)
                          | "System.UInt32" -> box (uint32 arg)
                          | "System.UInt64" -> box (uint64 arg)
                          | "System.Decimal" -> box (decimal arg)
                          | "System.Single" -> box (single arg)
                          | "System.Double" -> box (double arg)
                          | "System.Char" -> box (char arg)
                          | "System.Boolean" -> box (arg = "True")
                          | "System.String" -> box (string arg)
                          | s -> error(Error(FSComp.SR.etUnknownStaticArgumentKind(s, typeLogicalName), range0))

                      | _ ->
                          if sp.PUntaint ((fun sp -> sp.IsOptional), range) then 
                              match sp.PUntaint((fun sp -> sp.RawDefaultValue), range) with
                              | null -> error (Error(FSComp.SR.etStaticParameterRequiresAValue (spName, typeBeforeArgumentsName, typeBeforeArgumentsName, spName), range0))
                              | v -> v
                          else
                              error(Error(FSComp.SR.etProvidedTypeReferenceMissingArgument spName, range0)))
                    

            match TryApplyProvidedType(typeBeforeArguments, None, staticArgs, range0) with 
            | Some (typeWithArguments, checkTypeName) -> 
                checkTypeName() 
                Some typeWithArguments
            | None -> None

    /// Get the parts of a .NET namespace. Special rules: null means global, empty is not allowed.
    let GetPartsOfNamespaceRecover(namespaceName: string) = 
        if namespaceName=null then []
        elif  namespaceName.Length = 0 then ["<NonExistentNamespace>"]
        else splitNamespace namespaceName

    /// Get the parts of a .NET namespace. Special rules: null means global, empty is not allowed.
    let GetProvidedNamespaceAsPath (m, resolver: Tainted<ITypeProvider>, namespaceName: string) = 
        if namespaceName<>null && namespaceName.Length = 0 then
            errorR(Error(FSComp.SR.etEmptyNamespaceNotAllowed(DisplayNameOfTypeProvider(resolver.TypeProvider, m)), m))  

        GetPartsOfNamespaceRecover namespaceName

    /// Get the parts of the name that encloses the .NET type including nested types. 
    let GetFSharpPathToProvidedType (st: Tainted<ProvidedType>, range) = 
        // Can't use st.Fullname because it may be like IEnumerable<Something>
        // We want [System;Collections;Generic]
        let namespaceParts = GetPartsOfNamespaceRecover(st.PUntaint((fun st -> st.Namespace), range))
        let rec walkUpNestedClasses(st: Tainted<ProvidedType>, soFar) =
            match st with
            | Tainted.Null -> soFar
            | st -> walkUpNestedClasses(st.PApply((fun st ->st.DeclaringType), range), soFar) @ [st.PUntaint((fun st -> st.Name), range)]

        walkUpNestedClasses(st.PApply((fun st ->st.DeclaringType), range), namespaceParts)


    /// Get the ILAssemblyRef for a provided assembly. Do not take into account
    /// any type relocations or static linking for generated types.
    let GetOriginalILAssemblyRefOfProvidedAssembly (assembly: Tainted<ProvidedAssembly>, m) =
        let aname = assembly.PUntaint((fun assembly -> assembly.GetName()), m)
        ILAssemblyRef.FromAssemblyName aname

    /// Get the ILTypeRef for the provided type (including for nested types). Do not take into account
    /// any type relocations or static linking for generated types.
    let GetOriginalILTypeRefOfProvidedType (st: Tainted<ProvidedType>, range) = 
        
        let aref = GetOriginalILAssemblyRefOfProvidedAssembly (st.PApply((fun st -> st.Assembly), range), range)
        let scoperef = ILScopeRef.Assembly aref
        let enc, nm = ILPathToProvidedType (st, range)
        let tref = ILTypeRef.Create(scoperef, enc, nm)
        tref

    /// Get the ILTypeRef for the provided type (including for nested types). Take into account
    /// any type relocations or static linking for generated types.
    let GetILTypeRefOfProvidedType (st: Tainted<ProvidedType>, range) = 
        match st.PUntaint((fun st -> st.TryGetILTypeRef()), range) with 
        | Some ilTypeRef -> ilTypeRef
        | None -> GetOriginalILTypeRefOfProvidedType (st, range)

    type ProviderGeneratedType = ProviderGeneratedType of (*ilOrigTyRef*)ILTypeRef * (*ilRenamedTyRef*)ILTypeRef * ProviderGeneratedType list

    /// The table of information recording remappings from type names in the provided assembly to type
    /// names in the statically linked, embedded assembly, plus what types are nested in side what types.
    type ProvidedAssemblyStaticLinkingMap = 
        { ILTypeMap: Dictionary<ILTypeRef, ILTypeRef> }
        static member CreateNew() = 
            { ILTypeMap = Dictionary() }

    /// Check if this is a direct reference to a non-embedded generated type. This is not permitted at any name resolution.
    /// We check by seeing if the type is absent from the remapping context.
    let IsGeneratedTypeDirectReference (st: Tainted<ProvidedType>, m) =
        st.PUntaint((fun st -> st.TryGetTyconRef() |> Option.isNone), m)

#endif
