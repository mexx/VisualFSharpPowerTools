﻿namespace FSharpVSPowerTools

open System

[<RequireQualifiedAccess>]
module Seq =
    let tryHead s =
        if Seq.isEmpty s then None else Some (Seq.head s)

[<RequireQualifiedAccess>]
module Option =
    let inline ofNull value =
        if obj.ReferenceEquals(value, null) then None else Some value

    let inline ofNullable (value: Nullable<'a>) =
        if value.HasValue then Some value.Value else None

    let inline toNullable (value: 'a option) =
        match value with
        | Some x -> Nullable<_> x
        | None -> Nullable<_> ()

    let inline attempt (f: unit -> 'a) = try Some <| f() with _ -> None

    /// Gets the value associated with the option or the supplied default value.
    let inline getOrElse v =
        function
        | Some x -> x
        | None -> v

    /// Gets the option if Some x, otherwise the supplied default value.
    let inline orElse v =
        function
        | Some x -> Some x
        | None -> v
    
/// Maybe computation expression builder, copied from ExtCore library
/// https://github.com/jack-pappas/ExtCore/blob/master/ExtCore/Control.fs
[<Sealed>]
type MaybeBuilder () =
    // 'T -> M<'T>
    member inline x.Return value: 'T option =
        Some value

    // M<'T> -> M<'T>
    member inline x.ReturnFrom value: 'T option =
        value

    // unit -> M<'T>
    member inline x.Zero (): unit option =
        Some ()     // TODO: Should this be None?

    // (unit -> M<'T>) -> M<'T>
    member x.Delay (f: unit -> 'T option): 'T option =
        f ()

    // M<'T> -> M<'T> -> M<'T>
    // or
    // M<unit> -> M<'T> -> M<'T>
    member inline x.Combine (r1, r2: 'T option): 'T option =
        match r1 with
        | None ->
            None
        | Some () ->
            r2

    // M<'T> * ('T -> M<'U>) -> M<'U>
    member inline x.Bind (value, f: 'T -> 'U option): 'U option =
        Option.bind f value

    // 'T * ('T -> M<'U>) -> M<'U> when 'U :> IDisposable
    member x.Using (resource: ('T :> System.IDisposable), body: _ -> _ option): _ option =
        try body resource
        finally
            if not <| obj.ReferenceEquals (null, box resource) then
                resource.Dispose ()

    // (unit -> bool) * M<'T> -> M<'T>
    member x.While (guard, body: _ option): _ option =
        if guard () then
            // OPTIMIZE: This could be simplified so we don't need to make calls to Bind and While.
            x.Bind (body, (fun () -> x.While (guard, body)))
        else
            x.Zero ()

    // seq<'T> * ('T -> M<'U>) -> M<'U>
    // or
    // seq<'T> * ('T -> M<'U>) -> seq<M<'U>>
    member x.For (sequence: seq<_>, body: 'T -> unit option): _ option =
        // OPTIMIZE: This could be simplified so we don't need to make calls to Using, While, Delay.
        x.Using (sequence.GetEnumerator (), fun enum ->
            x.While (
                enum.MoveNext,
                x.Delay (fun () ->
                    body enum.Current)))

[<AutoOpen; CompilationRepresentation (CompilationRepresentationFlags.ModuleSuffix)>]
module Pervasive =
    let inline debug msg = Printf.kprintf System.Diagnostics.Debug.WriteLine msg
    let inline fail msg = Printf.kprintf System.Diagnostics.Debug.Fail msg
    let maybe = MaybeBuilder()
    
    let tryCast<'a> (o: obj): 'a option = 
        match o with
        | null -> None
        | :? 'a as a -> Some a
        | _ -> fail "Cannot cast %O to %O" (o.GetType()) typeof<'a>.Name; None


    open System.Threading
    
    let synchronize f = 
        let ctx = SynchronizationContext.Current
        
        let thread = 
            match ctx with
            | null -> null // saving a thread-local access
            | _ -> Thread.CurrentThread
        f (fun g arg -> 
            let nctx = SynchronizationContext.Current
            match ctx, nctx with
            | null, _ -> g arg
            | _, _ when Object.Equals(ctx, nctx) && thread.Equals(Thread.CurrentThread) -> g arg
            | _ -> ctx.Post((fun _ -> g (arg)), null))

    type Microsoft.FSharp.Control.Async with
        static member EitherEvent(ev1: IObservable<'a>, ev2: IObservable<'b>) = 
            synchronize (fun f -> 
                Async.FromContinuations((fun (cont, _econt, _ccont) -> 
                    let rec callback1 = 
                        (fun value -> 
                        remover1.Dispose()
                        remover2.Dispose()
                        f cont (Choice1Of2(value)))
                    
                    and callback2 = 
                        (fun value -> 
                        remover1.Dispose()
                        remover2.Dispose()
                        f cont (Choice2Of2(value)))
                    
                    and remover1: IDisposable = ev1.Subscribe(callback1)
                    and remover2: IDisposable = ev2.Subscribe(callback2)
                    ())))

    open System.IO

    type Path with
        static member GetFullPathSafe path =
            try Path.GetFullPath path
            with _ -> path

    open Microsoft.FSharp.Compiler.SourceCodeServices

    type SymbolScope = 
        | File
        | Project

    type FSharpSymbol with
        member x.Scope =
            let isPrivateToFile = 
                match x with 
                | :? FSharpMemberFunctionOrValue as m -> not m.IsModuleValueOrMember || m.Accessibility.IsPrivate
                | :? FSharpEntity as m -> m.Accessibility.IsPrivate
                | :? FSharpGenericParameter -> true
                | :? FSharpUnionCase as m -> m.Accessibility.IsPrivate
                | :? FSharpField as m -> m.Accessibility.IsPrivate
                | _ -> false
            if isPrivateToFile then SymbolScope.File else SymbolScope.Project
