#if !NO_EXTENSIONTYPING

// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

// Extension typing, validation of extension types, etc.

module FSharp.Compiler.ExtensionTyping

    open System
    open System.Collections.Generic
    open Microsoft.FSharp.Core.CompilerServices
    open FSharp.Compiler.AbstractIL.IL
    open FSharp.Compiler.Range

    type TypeProviderDesignation = TypeProviderDesignation of string

    /// Raised when a type provider has thrown an exception.    
    exception ProvidedTypeResolution of range * exn

    /// Raised when an type provider has thrown an exception.    
    exception ProvidedTypeResolutionNoRange of exn

    /// Get the list of relative paths searched for type provider design-time components
    val toolingCompatiblePaths: unit -> string list

    /// Carries information about the type provider resolution environment.
    type ResolutionEnvironment =
      {
        /// The folder from which an extension provider is resolving from. This is typically the project folder.
        resolutionFolder            : string
        /// Output file name
        outputFile                  : string option
        /// Whether or not the --showextensionresolution flag was supplied to the compiler.
        showResolutionMessages      : bool
        
        /// All referenced assemblies, including the type provider itself, and possibly other type providers.
        referencedAssemblies        : string[]

        /// The folder for temporary files
        temporaryFolder             : string
      }

    /// Given an extension type resolver, supply a human-readable name suitable for error messages.
    val DisplayNameOfTypeProvider : Tainted<Microsoft.FSharp.Core.CompilerServices.ITypeProvider> * range -> string

     /// The context used to interpret information in the closure of System.Type, System.MethodInfo and other 
     /// info objects coming from the type provider.
     ///
     /// At the moment this is the "Type --> ILTypeRef" and "Type --> Tycon" remapping 
     /// context for generated types (it is empty for erased types). This is computed from
     /// while processing the [<Generate>] declaration related to the type.
     ///
     /// Immutable (after type generation for a [<Generate>] declaration populates the dictionaries).
     ///
     /// The 'obj' values are all TyconRef, but obj is used due to a forward reference being required. Not particularly
     /// pleasant, but better than intertwining the whole "ProvidedType" with the TAST structure.
    [<Sealed>]
    type ProvidedTypeContext =

        member TryGetILTypeRef : System.Type -> ILTypeRef option

        member TryGetTyconRef : System.Type -> obj option

        static member Empty : ProvidedTypeContext 

        static member Create : Dictionary<System.Type,ILTypeRef> * Dictionary<System.Type,obj (* TyconRef *) > -> ProvidedTypeContext 

        member GetDictionaries : unit -> Dictionary<System.Type,ILTypeRef> * Dictionary<System.Type,obj (* TyconRef *) > 

        /// Map the TyconRef objects, if any
        member RemapTyconRefs : (obj -> obj) -> ProvidedTypeContext 

    type [<AllowNullLiteral; Class>] 
        ProvidedType =
        new : x: System.Type * ctxt: ProvidedTypeContext -> ProvidedType
        inherit ProvidedMemberInfo
        abstract member IsSuppressRelocate : bool
        abstract member IsErased : bool
        abstract member IsGenericType : bool
        abstract member Namespace : string
        abstract member FullName : string
        abstract member IsArray : bool
        abstract member GetInterfaces : unit -> ProvidedType[]
        member Assembly : ProvidedAssembly
        abstract member BaseType : ProvidedType
        abstract member GetNestedType : string -> ProvidedType
        abstract member GetNestedTypes : unit -> ProvidedType[]
        abstract member GetAllNestedTypes : unit -> ProvidedType[]
        abstract member GetMethods : unit -> ProvidedMethodInfo[]
        member GetFields : unit -> ProvidedFieldInfo[]
        member GetField : string -> ProvidedFieldInfo
        abstract member GetProperties : unit -> ProvidedPropertyInfo[]
        abstract member GetProperty : string -> ProvidedPropertyInfo
        member GetEvents : unit -> ProvidedEventInfo[]
        member GetEvent : string -> ProvidedEventInfo
        member GetConstructors : unit -> ProvidedConstructorInfo[]
        member RawSystemType : System.Type
        abstract member GetStaticParameters : ITypeProvider -> ProvidedParameterInfo[]
        abstract member ApplyStaticArguments: ITypeProvider * string[] * obj[] -> ProvidedType
        abstract member GetGenericTypeDefinition : unit -> ProvidedType
        abstract member IsVoid : bool
        abstract member IsGenericParameter : bool
        abstract member IsValueType : bool
        abstract member IsByRef : bool
        abstract member IsPointer : bool
        abstract member IsEnum : bool
        abstract member IsInterface : bool
        abstract member IsClass : bool
        abstract member IsSealed : bool
        abstract member IsAbstract : bool
        abstract member IsPublic : bool
        abstract member IsNestedPublic : bool
        abstract member GenericParameterPosition : int
        abstract member GetElementType : unit -> ProvidedType
        abstract member GetGenericArguments : unit -> ProvidedType[]
        abstract member GetArrayRank : unit -> int
        abstract member GetEnumUnderlyingType : unit -> ProvidedType
        static member Void : ProvidedType
        static member CreateNoContext : Type -> ProvidedType
        member TryGetILTypeRef : unit -> ILTypeRef option
        member TryGetTyconRef : unit -> obj option
        abstract member ApplyContext : ProvidedTypeContext -> ProvidedType
        member Context : ProvidedTypeContext 
        interface IProvidedCustomAttributeProvider
        static member TaintedEquals : Tainted<ProvidedType> * Tainted<ProvidedType> -> bool 

    and [<AllowNullLiteral>] 
        IProvidedCustomAttributeProvider =
        abstract GetHasTypeProviderEditorHideMethodsAttribute : provider:ITypeProvider -> bool
        abstract GetDefinitionLocationAttribute : provider:ITypeProvider -> (string * int * int) option 
        abstract GetXmlDocAttributes : provider:ITypeProvider -> string[]
        abstract GetAttributeConstructorArgs: provider:ITypeProvider * attribName:string -> (obj option list * (string * obj option) list) option
        
    and [<AllowNullLiteral; Sealed; Class>] 
        ProvidedAssembly = 
        member GetName : unit -> System.Reflection.AssemblyName
        member FullName : string
        member GetManifestModuleContents : ITypeProvider -> byte[]
        member Handle : System.Reflection.Assembly

    and [<AllowNullLiteral;AbstractClass>] 
        ProvidedMemberInfo = 
        abstract member Name :string
        abstract member DeclaringType : ProvidedType
        interface IProvidedCustomAttributeProvider 

    and [<AllowNullLiteral;AbstractClass>] 
        ProvidedMethodBase = 
        inherit ProvidedMemberInfo
        abstract member IsGenericMethod : bool
        abstract member IsStatic : bool
        abstract member IsFamily : bool
        abstract member IsFamilyAndAssembly : bool
        abstract member IsFamilyOrAssembly : bool
        abstract member IsVirtual : bool
        abstract member IsFinal : bool
        abstract member IsPublic : bool
        abstract member IsAbstract : bool
        abstract member IsHideBySig : bool
        abstract member IsConstructor : bool
        abstract member GetParameters : unit -> ProvidedParameterInfo[]
        abstract member GetGenericArguments : unit -> ProvidedType[]
        member GetStaticParametersForMethod : ITypeProvider -> ProvidedParameterInfo[]
        static member TaintedGetHashCode : Tainted<ProvidedMethodBase> -> int
        static member TaintedEquals : Tainted<ProvidedMethodBase> * Tainted<ProvidedMethodBase> -> bool 

    and [<AllowNullLiteral; Class>]
        ProvidedMethodInfo =
        new: x: System.Reflection.MethodInfo * ctxt: ProvidedTypeContext -> ProvidedMethodInfo
        inherit ProvidedMethodBase
        abstract member ReturnType : ProvidedType
        abstract member MetadataToken : int

    and [<AllowNullLiteral; Class>] 
        ProvidedParameterInfo =
        new: x: System.Reflection.ParameterInfo * ctxt: ProvidedTypeContext -> ProvidedParameterInfo
        abstract member Name :string
        abstract member ParameterType : ProvidedType
        abstract member IsIn : bool
        abstract member IsOut : bool
        abstract member IsOptional : bool
        abstract member RawDefaultValue : obj
        abstract member HasDefaultValue : bool
        interface IProvidedCustomAttributeProvider 

    and [<AllowNullLiteral; Class; Sealed>] 
        ProvidedFieldInfo = 
        inherit ProvidedMemberInfo
        member IsInitOnly : bool
        member IsStatic : bool
        member IsSpecialName : bool
        member IsLiteral : bool
        member GetRawConstantValue : unit -> obj
        member FieldType : ProvidedType
        member IsPublic : bool
        member IsFamily : bool
        member IsFamilyAndAssembly : bool
        member IsFamilyOrAssembly : bool
        member IsPrivate : bool
        static member TaintedEquals : Tainted<ProvidedFieldInfo> * Tainted<ProvidedFieldInfo> -> bool 

    and [<AllowNullLiteral; Class>] 
        ProvidedPropertyInfo =
        new: x: System.Reflection.PropertyInfo * ctxt: ProvidedTypeContext -> ProvidedPropertyInfo
        inherit ProvidedMemberInfo
        abstract member GetGetMethod : unit -> ProvidedMethodInfo
        abstract member GetSetMethod : unit -> ProvidedMethodInfo
        abstract member GetIndexParameters : unit -> ProvidedParameterInfo[]
        abstract member CanRead : bool
        abstract member CanWrite : bool
        abstract member PropertyType : ProvidedType
        static member TaintedGetHashCode : Tainted<ProvidedPropertyInfo> -> int
        static member TaintedEquals : Tainted<ProvidedPropertyInfo> * Tainted<ProvidedPropertyInfo> -> bool 

    and [<AllowNullLiteral; Class; Sealed>] 
        ProvidedEventInfo = 
        inherit ProvidedMemberInfo
        member GetAddMethod : unit -> ProvidedMethodInfo
        member GetRemoveMethod : unit -> ProvidedMethodInfo
        member EventHandlerType : ProvidedType
        static member TaintedGetHashCode : Tainted<ProvidedEventInfo> -> int
        static member TaintedEquals : Tainted<ProvidedEventInfo> * Tainted<ProvidedEventInfo> -> bool 

    and [<AllowNullLiteral; Class; Sealed>] 
        ProvidedConstructorInfo = 
        inherit ProvidedMethodBase
        
    [<RequireQualifiedAccess; Class; Sealed; AllowNullLiteral>]
    type ProvidedExpr =
        member Type : ProvidedType
        /// Convert the expression to a string for diagnostics
        member UnderlyingExpressionString : string

    [<RequireQualifiedAccess; Class; Sealed; AllowNullLiteral>]
    type ProvidedVar =
        member Type : ProvidedType
        member Name : string
        member IsMutable : bool
        static member Fresh : string * ProvidedType -> ProvidedVar
        override Equals : obj -> bool
        override GetHashCode : unit -> int

    /// Detect a provided new-array expression 
    val (|ProvidedNewArrayExpr|_|)   : ProvidedExpr -> (ProvidedType * ProvidedExpr[]) option

#if PROVIDED_ADDRESS_OF
    val (|ProvidedAddressOfExpr|_|)  : ProvidedExpr -> ProvidedExpr option
#endif

    /// Detect a provided new-object expression 
    val (|ProvidedNewObjectExpr|_|)     : ProvidedExpr -> (ProvidedConstructorInfo * ProvidedExpr[]) option

    /// Detect a provided while-loop expression 
    val (|ProvidedWhileLoopExpr|_|) : ProvidedExpr -> (ProvidedExpr * ProvidedExpr) option

    /// Detect a provided new-delegate expression 
    val (|ProvidedNewDelegateExpr|_|) : ProvidedExpr -> (ProvidedType * ProvidedVar[] * ProvidedExpr) option

    /// Detect a provided expression which is a for-loop over integers
    val (|ProvidedForIntegerRangeLoopExpr|_|) : ProvidedExpr -> (ProvidedVar * ProvidedExpr * ProvidedExpr * ProvidedExpr) option

    /// Detect a provided sequential expression 
    val (|ProvidedSequentialExpr|_|)    : ProvidedExpr -> (ProvidedExpr * ProvidedExpr) option

    /// Detect a provided try/with expression 
    val (|ProvidedTryWithExpr|_|)       : ProvidedExpr -> (ProvidedExpr * ProvidedVar * ProvidedExpr * ProvidedVar * ProvidedExpr) option

    /// Detect a provided try/finally expression 
    val (|ProvidedTryFinallyExpr|_|)    : ProvidedExpr -> (ProvidedExpr * ProvidedExpr) option

    /// Detect a provided lambda expression 
    val (|ProvidedLambdaExpr|_|)     : ProvidedExpr -> (ProvidedVar * ProvidedExpr) option

    /// Detect a provided call expression 
    val (|ProvidedCallExpr|_|) : ProvidedExpr -> (ProvidedExpr option * ProvidedMethodInfo * ProvidedExpr[]) option

    /// Detect a provided constant expression 
    val (|ProvidedConstantExpr|_|)   : ProvidedExpr -> (obj * ProvidedType) option

    /// Detect a provided default-value expression 
    val (|ProvidedDefaultExpr|_|)    : ProvidedExpr -> ProvidedType option

    /// Detect a provided new-tuple expression 
    val (|ProvidedNewTupleExpr|_|)   : ProvidedExpr -> ProvidedExpr[] option

    /// Detect a provided tuple-get expression 
    val (|ProvidedTupleGetExpr|_|)   : ProvidedExpr -> (ProvidedExpr * int) option

    /// Detect a provided type-as expression 
    val (|ProvidedTypeAsExpr|_|)      : ProvidedExpr -> (ProvidedExpr * ProvidedType) option

    /// Detect a provided type-test expression 
    val (|ProvidedTypeTestExpr|_|)      : ProvidedExpr -> (ProvidedExpr * ProvidedType) option

    /// Detect a provided 'let' expression 
    val (|ProvidedLetExpr|_|)      : ProvidedExpr -> (ProvidedVar * ProvidedExpr * ProvidedExpr) option

    /// Detect a provided 'set variable' expression 
    val (|ProvidedVarSetExpr|_|)      : ProvidedExpr -> (ProvidedVar * ProvidedExpr) option

    /// Detect a provided 'IfThenElse' expression 
    val (|ProvidedIfThenElseExpr|_|) : ProvidedExpr -> (ProvidedExpr * ProvidedExpr * ProvidedExpr) option

    /// Detect a provided 'Var' expression 
    val (|ProvidedVarExpr|_|)  : ProvidedExpr -> ProvidedVar option

    /// Get the provided expression for a particular use of a method.
    val GetInvokerExpression : ITypeProvider * ProvidedMethodBase * ProvidedVar[] ->  ProvidedExpr

    /// Validate that the given provided type meets some of the rules for F# provided types
    val ValidateProvidedTypeAfterStaticInstantiation : range * Tainted<ProvidedType> * expectedPath : string[] * expectedName : string-> unit

    /// Try to apply a provided type to the given static arguments. If successful also return a function 
    /// to check the type name is as expected (this function is called by the caller of TryApplyProvidedType
    /// after other checks are made).
    val TryApplyProvidedType : typeBeforeArguments:Tainted<ProvidedType> * optGeneratedTypePath: string list option * staticArgs:obj[]  * range -> (Tainted<ProvidedType> * (unit -> unit)) option

    /// Try to apply a provided method to the given static arguments. 
    val TryApplyProvidedMethod : methBeforeArguments:Tainted<ProvidedMethodBase> * staticArgs:obj[]  * range -> Tainted<ProvidedMethodBase> option

    /// Try to resolve a type in the given extension type resolver
    val TryResolveProvidedType : Tainted<ITypeProvider> * range * string[] * typeName: string -> Tainted<ProvidedType> option

    /// Try to resolve a type in the given extension type resolver
    val TryLinkProvidedType : Tainted<ITypeProvider> * string[] * typeLogicalName: string * range: range -> Tainted<ProvidedType> option

    /// Get the parts of a .NET namespace. Special rules: null means global, empty is not allowed.
    val GetProvidedNamespaceAsPath : range * Tainted<ITypeProvider> * string -> string list

    /// Decompose the enclosing name of a type (including any class nestings) into a list of parts.
    /// e.g. System.Object -> ["System"; "Object"]
    val GetFSharpPathToProvidedType : Tainted<ProvidedType> * range:range-> string list
    
    /// Get the ILTypeRef for the provided type (including for nested types). Take into account
    /// any type relocations or static linking for generated types.
    val GetILTypeRefOfProvidedType : Tainted<ProvidedType> * range:range -> FSharp.Compiler.AbstractIL.IL.ILTypeRef

    /// Get the ILTypeRef for the provided type (including for nested types). Do not take into account
    /// any type relocations or static linking for generated types.
    val GetOriginalILTypeRefOfProvidedType : Tainted<ProvidedType> * range:range -> FSharp.Compiler.AbstractIL.IL.ILTypeRef


    /// Represents the remapping information for a generated provided type and its nested types.
    ///
    /// There is one overall tree for each root 'type X = ... type generation expr...' specification.
    type ProviderGeneratedType = ProviderGeneratedType of (*ilOrigTyRef*)ILTypeRef * (*ilRenamedTyRef*)ILTypeRef * ProviderGeneratedType list

    /// The table of information recording remappings from type names in the provided assembly to type
    /// names in the statically linked, embedded assembly, plus what types are nested in side what types.
    type ProvidedAssemblyStaticLinkingMap = 
        {  /// The table of remappings from type names in the provided assembly to type
           /// names in the statically linked, embedded assembly.
           ILTypeMap: System.Collections.Generic.Dictionary<ILTypeRef, ILTypeRef> }
        
        /// Create a new static linking map, ready to populate with data.
        static member CreateNew : unit -> ProvidedAssemblyStaticLinkingMap

    /// Check if this is a direct reference to a non-embedded generated type. This is not permitted at any name resolution.
    /// We check by seeing if the type is absent from the remapping context.
    val IsGeneratedTypeDirectReference         : Tainted<ProvidedType> * range -> bool
    
    [<AutoOpen>]
    module Shim =
        
        type IExtensionTypingProvider =
            
             /// Find and instantiate the set of ITypeProvider components for the given assembly reference
            abstract InstantiateTypeProvidersOfAssembly : 
              runtimeAssemblyFilename: string 
              * ilScopeRefOfRuntimeAssembly:ILScopeRef
              * designerAssemblyName: string 
              * ResolutionEnvironment 
              * bool
              * isInteractive: bool
              * systemRuntimeContainsType : (string -> bool)
              * systemRuntimeAssemblyVersion : System.Version
              * range -> Tainted<ITypeProvider> list

            abstract GetProvidedTypes : Tainted<IProvidedNamespace> * range -> Tainted<ProvidedType>[]
            abstract ResolveTypeName : Tainted<IProvidedNamespace> * string * range -> Tainted<ProvidedType>

        [<Sealed>]
        type DefaultExtensionTypingProvider =
            interface IExtensionTypingProvider

        val mutable ExtensionTypingProvider: IExtensionTypingProvider

#endif
