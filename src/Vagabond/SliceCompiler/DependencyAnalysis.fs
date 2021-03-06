﻿module internal Nessos.Vagabond.DependencyAnalysis

open System
open System.IO
open System.Collections.Generic
open System.Reflection

open Nessos.FsPickler

open Nessos.Vagabond.AssemblyNaming
open Nessos.Vagabond.AssemblyParser
open Nessos.Vagabond.SliceCompilerTypes

open Microsoft.FSharp.Reflection

/// Assembly-specific topological ordering for assembly dependencies
let getAssemblyOrdering (dependencies : Graph<Assembly>) : Assembly list =
    match tryGetTopologicalOrdering dependencies with
    | Choice1Of2 sorted -> sorted
    | Choice2Of2 cycle ->
        // graph not DAG, return an appropriate exception
        let cycle = cycle |> Seq.map (fun a -> a.GetName().Name) |> String.concat ", "
        raise <| new VagabondException(sprintf "Found circular dependencies: %s." cycle)

/// returns all type depndencies for supplied object graph
let gatherObjectDependencies (graph:obj) : Type [] * Assembly [] =
    let types = new HashSet<Type> ()
    let assemblies = new HashSet<Assembly> ()

    let rec traverseType (t : Type) =
        if t = null || types.Contains t then ()
        elif t.IsGenericType && not t.IsGenericTypeDefinition then
            types.Add(t.GetGenericTypeDefinition()) |> ignore
            for ga in t.GetGenericArguments() do
                traverseType ga

        elif t.IsArray || t.IsPointer || t.IsByRef then
            traverseType <| t.GetElementType()
        else  
            types.Add t |> ignore

    let typeGatherer =
        {
            new IObjectVisitor with
                member __.Visit<'T> (p : Pickler<'T>, value : 'T) =
                    if p.Kind > Kind.Primitive then
                        traverseType p.Type
                        if p.Kind > Kind.Value then
                            match box value with
                            | null -> ()
                            | :? Assembly as a -> assemblies.Add a |> ignore
                            | :? Type as t -> traverseType t
                            | :? MemberInfo as m when m.DeclaringType <> null -> traverseType m.DeclaringType
                            | o -> traverseType <| o.GetType()

                    true
        }

    do FsPickler.VisitObject(typeGatherer, graph)

    Seq.toArray types, Seq.toArray assemblies


/// assemblies ignored by Vagabond during assembly traversal
let private isIgnoredAssembly =
    let getPublicKey (a : Assembly) = a.GetName().GetPublicKey()
    let systemPkt = getPublicKey typeof<int>.Assembly
    let msPkt = getPublicKey typeof<int option>.Assembly
    let vagabondAssemblies = 
        hset [|
            typeof<int option>.Assembly
            typeof<Mono.Cecil.AssemblyDefinition>.Assembly
            typeof<Nessos.Vagabond.AssemblyParser.IAssemblyParserConfig>.Assembly
            typeof<Nessos.Vagabond.AssemblyId>.Assembly
        |]

    // ignore MS assemblies that are in circular dependency
    let cyclicLibs = hset [| "System.Web" ; "System.Web.Services" ; "System.Web.Design" |]

    fun (a:Assembly) ->
        if vagabondAssemblies.Contains a then true
        else
            let pkt = getPublicKey a
            pkt = systemPkt ||
            pkt = msPkt && cyclicLibs.Contains(a.GetName().Name)


/// locally resolve an assembly by qualified name
let private tryResolveAssembly (ignoreF : Assembly -> bool) (policy : AssemblyLookupPolicy) (state : DynamicAssemblyCompilerState option) (fullName : string) =
    match state |> Option.bind (fun s -> s.TryFindSliceInfo fullName) with
    | Some (_,info) -> Some info.Assembly
    | None ->
        let localAssembly =
            if AssemblyId.OfFullName(fullName).CanBeResolvedLocally policy then
                tryLoadAssembly fullName
            else
                tryGetLoadedAssembly fullName

        match localAssembly with
        | Some a when isIgnoredAssembly a || ignoreF a -> None
        | Some _ as r -> r
        | None when policy.HasFlag AssemblyLookupPolicy.RequireLocalDependenciesLoadedInAppDomain -> 
            let msg = sprintf "could not locate dependency '%s'. Try adding an explicit reference." fullName
            raise <| new VagabondException(msg)
        | None -> None

/// recursively traverse assembly dependency graph
let traverseDependencies ignoreF requireLoaded (state : DynamicAssemblyCompilerState option) (assemblies : seq<Assembly>) =

    let rec traverseDependencyGraph (graph : Map<AssemblyId, Assembly * Assembly list>) (remaining : Assembly list) =
        match remaining with
        | [] -> graph |> Map.toList |> List.map snd
        | a :: tail when graph.ContainsKey a.AssemblyId || isIgnoredAssembly a || ignoreF a -> traverseDependencyGraph graph tail
        | a :: tail -> 
            let dependencies = a.GetReferencedAssemblies() |> Array.choose (fun an -> tryResolveAssembly ignoreF requireLoaded state an.FullName) |> Array.toList
            traverseDependencyGraph (graph.Add(a.AssemblyId, (a, dependencies))) (dependencies @ tail)

    let dependencies =
        assemblies
        |> Seq.toList
        |> traverseDependencyGraph Map.empty
        |> getAssemblyOrdering

    // check for assemblies of identical qualified name
    match dependencies |> Seq.groupBy(fun a -> a.FullName) |> Seq.tryFind (fun (_,assemblies) -> Seq.length assemblies > 1) with
    | None -> ()
    | Some(name,_) -> 
        raise <| new VagabondException(sprintf "ran into duplicate assemblies of qualified name '%s'. This is not supported." name)

    dependencies


/// parse a collection of assemblies, identify the dynamic assemblies that require slice compilation
/// the dynamic assemblies are then parsed to Cecil and sorted topologically for correct compilation order.
let parseDynamicAssemblies (ignoreF : Assembly -> bool) (policy : AssemblyLookupPolicy) 
                            (state : DynamicAssemblyCompilerState) (assemblies : seq<Assembly>) =

    let isDynamicAssemblyRequiringCompilation (a : Assembly) =
        if a.IsDynamic then
            state.DynamicAssemblies.TryFind a.FullName 
            |> Option.forall (fun info -> info.HasFreshTypes)
        else
            false

    let rec traverse (graph : Map<AssemblyId, _>) (remaining : Assembly list) =
        match remaining with
        | [] -> graph
        | a :: rest when graph.ContainsKey a.AssemblyId || not <| isDynamicAssemblyRequiringCompilation a -> traverse graph rest
        | a :: rest ->
            // parse dynamic assembly
            let ((_,_,dependencies,_) as sliceData) = parseDynamicAssemblySlice state a

            let dependencies = dependencies |> List.choose (tryResolveAssembly ignoreF policy (Some state))

            let graph' = graph.Add(a.AssemblyId, (a, dependencies, sliceData))
                
            traverse graph' (rest @ dependencies)


    // topologically sort output
    let dynamicAssemblies = traverse Map.empty <| Seq.toList assemblies

    dynamicAssemblies
    |> Seq.map (function KeyValue(_, (a, deps,_)) -> a, deps |> List.filter (fun a -> a.IsDynamic))
    |> Seq.toList
    |> getAssemblyOrdering
    |> List.map (fun a -> let _,_,data = dynamicAssemblies.[a.AssemblyId] in data)


/// Assembly dependency and its directly referenced types
type Dependencies = (Assembly * seq<Type>) list

/// compute dependencies for supplied object graph
let computeDependencies (obj:obj) : Dependencies =
    let types, assemblies = gatherObjectDependencies obj
        
    types
    |> Seq.groupBy (fun t -> t.Assembly)
    |> Seq.append (assemblies |> Seq.map (fun a -> a, Seq.empty))
    |> Seq.toList


/// determines the assemblies that require slice compilation based on given dependency input
let getDynamicDependenciesRequiringCompilation (state : DynamicAssemblyCompilerState) (dependencies : Dependencies) =
    dependencies
    |> Seq.filter(fun (a,types) ->
        if a.IsDynamic then
            match state.DynamicAssemblies.TryFind a.FullName with
            | Some info -> types |> Seq.exists(fun t -> not <| info.TypeIndex.ContainsKey t.FullName)
            | None -> true
        else
            false)
    |> Seq.map fst
    |> Seq.toArray


/// reassigns assemblies so that the correct assembly slices are matched
let remapDependencies ignoreF policy (state : DynamicAssemblyCompilerState) (dependencies : Dependencies) =
    let remap (a : Assembly, ts : seq<Type>) =
        if a.IsDynamic then
            match state.DynamicAssemblies.TryFind a.FullName with
            | None -> raise <| new VagabondException(sprintf "no slices have been created for assembly '%s'." a.FullName)
            | Some info ->
                let remapType (t : Type) =
                    match info.TypeIndex.TryFind t.FullName with
                    | None | Some (InNoSlice | InAllSlices) -> 
                        raise <| new VagabondException(sprintf "no slice corresponds to dynamic type '%O'." t)

                    | Some (InSpecificSlice slice) -> slice.Assembly

                Seq.map remapType ts
        else Seq.singleton a

    dependencies |> Seq.collect remap |> traverseDependencies ignoreF policy (Some state)