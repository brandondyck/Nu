﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2020.

namespace Nu
open System
open System.Collections.Generic
open System.IO
open System.Numerics
open System.Threading
open FSharpx.Collections
open SDL2
open Prime
open Nu

[<AutoOpen; ModuleBinding>]
module WorldModule2 =

    (* Performance Timers *)
    let private TotalTimer = Diagnostics.Stopwatch ()
    let private InputTimer = Diagnostics.Stopwatch ()
    let private PhysicsTimer = Diagnostics.Stopwatch ()
    let private UpdateTimer = Diagnostics.Stopwatch ()
    let private UpdateGatherTimer = Diagnostics.Stopwatch ()
    let private UpdateGameTimer = Diagnostics.Stopwatch ()
    let private UpdateScreensTimer = Diagnostics.Stopwatch ()
    let private UpdateGroupsTimer = Diagnostics.Stopwatch ()
    let private UpdateEntitiesTimer = Diagnostics.Stopwatch ()
    let private PostUpdateTimer = Diagnostics.Stopwatch ()
    let private PostUpdateGatherTimer = Diagnostics.Stopwatch ()
    let private PostUpdateGameTimer = Diagnostics.Stopwatch ()
    let private PostUpdateScreensTimer = Diagnostics.Stopwatch ()
    let private PostUpdateGroupsTimer = Diagnostics.Stopwatch ()
#if !DISABLE_ENTITY_POST_UPDATE
    let private PostUpdateEntitiesTimer = Diagnostics.Stopwatch ()
#endif
    let private TaskletsTimer = Diagnostics.Stopwatch ()
    let private DestructionTimer = Diagnostics.Stopwatch ()
    let private PerProcessTimer = Diagnostics.Stopwatch ()
    let private PreProcessTimer = Diagnostics.Stopwatch ()
    let private PostProcessTimer = Diagnostics.Stopwatch ()
    let private RenderGatherTimer = Diagnostics.Stopwatch ()
    let private RenderEntitiesTimer = Diagnostics.Stopwatch ()
    let private RenderTimer = Diagnostics.Stopwatch ()
    let private AudioTimer = Diagnostics.Stopwatch ()

    (* Transition Values *)
    let private ScreenTransitionMouseLeftId = Gen.id
    let private ScreenTransitionMouseCenterId = Gen.id
    let private ScreenTransitionMouseRightId = Gen.id
    let private ScreenTransitionMouseX1Id = Gen.id
    let private ScreenTransitionMouseX2Id = Gen.id
    let private ScreenTransitionKeyboardKeyId = Gen.id

    (* Cached HashSets *)
    type OctelementComparer () =
        interface IEqualityComparer<Octelement<Entity>> with
            member this.GetHashCode element = element.HashCode
            member this.Equals (element, element2) = Address.equals element.Entry.EntityAddress element2.Entry.EntityAddress
    let private CachedHashSet3d = HashSet (OctelementComparer ())
    let private CachedHashSet2d = HashSet HashIdentity.Structural

    type World with

        static member internal makeQuadtree () =
            Quadtree.make Constants.Engine.QuadtreeGranularity Constants.Engine.QuadtreeDepth Constants.Engine.QuadtreeBounds

        static member internal makeOctree () =
            Octree.make Constants.Engine.OctreeGranularity Constants.Engine.OctreeDepth Constants.Engine.OctreeBounds

        static member internal rebuildQuadtree world =
            let omniEntities =
                match World.getOmniScreenOpt world with
                | Some screen -> World.getGroups screen world |> Seq.map (flip World.getEntitiesFlattened world) |> Seq.concat
                | None -> Seq.empty
            let selectedEntities =
                match World.getSelectedScreenOpt world with
                | Some screen -> World.getGroups screen world |> Seq.map (flip World.getEntitiesFlattened world) |> Seq.concat
                | None -> Seq.empty
            let entities = Seq.append omniEntities selectedEntities
            let quadtree = World.makeQuadtree ()
            for entity in entities do
                let bounds = entity.GetBounds world
                let presence = entity.GetPresence world
                if entity.GetIs2d world then
                    Quadtree.addElement presence bounds.Box2 entity quadtree
            quadtree

        static member internal rebuildOctree world =
            let omniEntities =
                match World.getOmniScreenOpt world with
                | Some screen -> World.getGroups screen world |> Seq.map (flip World.getEntitiesFlattened world) |> Seq.concat
                | None -> Seq.empty
            let selectedEntities =
                match World.getSelectedScreenOpt world with
                | Some screen -> World.getGroups screen world |> Seq.map (flip World.getEntitiesFlattened world) |> Seq.concat
                | None -> Seq.empty
            let entities = Seq.append omniEntities selectedEntities
            let octree = World.makeOctree ()
            for entity in entities do
                let bounds = entity.GetBounds world
                let static_ = entity.GetStatic world
                let light = entity.GetLight world
                let presence = entity.GetPresence world
                if not (entity.GetIs2d world) then
                    let element = Octelement.make static_ light presence entity
                    Octree.addElement bounds element octree
            octree

        /// Resolve a relation to an address in the current script context.
        static member resolve<'a> (relation : 'a Relation) world =
            let scriptContext = World.getScriptContext world
            let address = Relation.resolve scriptContext.SimulantAddress relation
            address
    
        /// Relate an address to the current script context.
        static member relate<'a> (address : 'a Address) world =
            let scriptContext = World.getScriptContext world
            let address = Relation.relate scriptContext.SimulantAddress address
            address

        /// Resolve a relation to an address in the current script context in script.
        [<FunctionBinding "resolve">]
        static member internal resolveViaScript (relation : obj Relation) world =
            World.resolve relation world

        /// Relate an address to the current script context in script.
        [<FunctionBinding "relate">]
        static member internal relateViaScript (address : obj Address) world =
            World.relate address world

        /// Select the given screen without transitioning, even if another transition is taking place.
        static member internal selectScreenOpt transitionStateAndScreenOpt world =
            let world =
                match World.getSelectedScreenOpt world with
                | Some selectedScreen ->
                    let eventTrace = EventTrace.debug "World" "selectScreen" "Deselecting" EventTrace.empty
                    World.publish () (Events.Deselecting --> selectedScreen) eventTrace selectedScreen world
                | None -> world
            match transitionStateAndScreenOpt with
            | Some (transitionState, screen) ->
                let world = World.setScreenTransitionStatePlus transitionState screen world
                let world = World.setSelectedScreen screen world
                let eventTrace = EventTrace.debug "World" "selectScreen" "Select" EventTrace.empty
                World.publish () (Events.Select --> screen) eventTrace screen world
            | None ->
                World.setSelectedScreenOpt None world

        /// Select the given screen without transitioning, even if another transition is taking place.
        [<FunctionBinding>]
        static member selectScreen transitionState screen world =
            World.selectScreenOpt (Some (transitionState, screen)) world

        /// Try to check that the selected screen is idling; that is, neither transitioning in or
        /// out via another screen.
        [<FunctionBinding>]
        static member tryGetIsSelectedScreenIdling world =
            match World.getSelectedScreenOpt world with
            | Some selectedScreen -> Some (selectedScreen.IsIdling world)
            | None -> None

        /// Try to check that the selected screen is transitioning.
        [<FunctionBinding>]
        static member tryGetIsSelectedScreenTransitioning world =
            Option.map not (World.tryGetIsSelectedScreenIdling world)

        /// Check that the selected screen is idling; that is, neither transitioning in or
        /// out via another screen (failing with an exception if no screen is selected).
        [<FunctionBinding>]
        static member isSelectedScreenIdling world =
            match World.tryGetIsSelectedScreenIdling world with
            | Some answer -> answer
            | None -> failwith "Cannot query state of non-existent selected screen."

        /// Check that the selected screen is transitioning (failing with an exception if no screen
        /// is selected).
        [<FunctionBinding>]
        static member isSelectedScreenTransitioning world =
            not (World.isSelectedScreenIdling world)

        /// Set screen transition state, enabling or disabling input events respectively.
        static member private setScreenTransitionStatePlus state (screen : Screen) world =
            let world = screen.SetTransitionState state world
            match state with
            | IdlingState ->
                let world = World.unsubscribe ScreenTransitionMouseLeftId world
                let world = World.unsubscribe ScreenTransitionMouseCenterId world
                let world = World.unsubscribe ScreenTransitionMouseRightId world
                let world = World.unsubscribe ScreenTransitionMouseX1Id world
                let world = World.unsubscribe ScreenTransitionMouseX2Id world
                let world = World.unsubscribe ScreenTransitionKeyboardKeyId world
                world
            | IncomingState
            | OutgoingState ->
                let world = World.subscribePlus ScreenTransitionMouseLeftId World.handleAsSwallow (stoa<MouseButtonData> "Mouse/Left/@/Event") Simulants.Game world |> snd
                let world = World.subscribePlus ScreenTransitionMouseCenterId World.handleAsSwallow (stoa<MouseButtonData> "Mouse/Center/@/Event") Simulants.Game world |> snd
                let world = World.subscribePlus ScreenTransitionMouseRightId World.handleAsSwallow (stoa<MouseButtonData> "Mouse/Right/@/Event") Simulants.Game world |> snd
                let world = World.subscribePlus ScreenTransitionMouseX1Id World.handleAsSwallow (stoa<MouseButtonData> "Mouse/X1/@/Event") Simulants.Game world |> snd
                let world = World.subscribePlus ScreenTransitionMouseX2Id World.handleAsSwallow (stoa<MouseButtonData> "Mouse/X2/@/Event") Simulants.Game world |> snd
                let world = World.subscribePlus ScreenTransitionKeyboardKeyId World.handleAsSwallow (stoa<KeyboardKeyData> "KeyboardKey/@/Event") Simulants.Game world |> snd
                world

        static member private updateScreenTransition3 transitionType (selectedScreen : Screen) world =
            // NOTE: we do not immediately transition when transition time is zero because we only want screen
            // transitions to happen outside the update loop!
            // NOTE: transitions always take one additional frame because it needs to render frame 0 and frame MAX + 1 for
            // full opacity if fading and and an extra frame for the render messages to actually get processed.
            let transition =
                match transitionType with
                | Incoming -> selectedScreen.GetIncoming world
                | Outgoing -> selectedScreen.GetOutgoing world
            let transitionUpdates = selectedScreen.GetTransitionUpdates world
            if transitionUpdates = transition.TransitionLifeTime + 1L then
                (true, selectedScreen.SetTransitionUpdates 0L world)
            elif transitionUpdates > transition.TransitionLifeTime then
                Log.debug ("TransitionLifeTime for screen '" + scstring selectedScreen.ScreenAddress + "' must be a consistent multiple of UpdateRate.")
                (true, selectedScreen.SetTransitionUpdates 0L world)
            else (false, selectedScreen.SetTransitionUpdates (transitionUpdates + World.getUpdateRate world) world)

        static member private updateScreenIdling3 splash (selectedScreen : Screen) world =
            // NOTE: we do not immediately transition when transition time is zero because we only want screen
            // transitions to happen outside the update loop!
            // NOTE: transitions always take one additional frame because it needs to render frame 0 and frame MAX + 1 for
            // full opacity if fading and and an extra frame for the render messages to actually get processed.
            let transitionUpdates = selectedScreen.GetTransitionUpdates world
            if transitionUpdates = splash.IdlingTime + 1L then
                (true, selectedScreen.SetTransitionUpdates 0L world)
            elif transitionUpdates > splash.IdlingTime then
                Log.debug ("IdlingTimeOpt for screen '" + scstring selectedScreen.ScreenAddress + "' must be Some consistent multiple of UpdateRate or None.")
                (true, selectedScreen.SetTransitionUpdates 0L world)
            else (false, selectedScreen.SetTransitionUpdates (transitionUpdates + World.getUpdateRate world) world)

        static member private updateScreenIncoming (selectedScreen : Screen) world =
            match World.getLiveness world with
            | Live ->
                let world =
                    if selectedScreen.GetTransitionUpdates world = 0L then
                        let world =
                            match (selectedScreen.GetIncoming world).SongOpt with
                            | Some playSong ->
                                match World.getCurrentSongOpt world with
                                | Some song when assetEq song.Song playSong.Song -> world // do nothing when song is the same
                                | _ -> World.playSong playSong.FadeInMs playSong.FadeOutMs playSong.Volume 0.0 playSong.Song world // play song when song is different
                            | None -> world
                        let eventTrace = EventTrace.debug "World" "updateScreenIncoming" "IncomingStart" EventTrace.empty
                        World.publish () (Events.IncomingStart --> selectedScreen) eventTrace selectedScreen world
                    else world
                match World.getLiveness world with
                | Live ->
                    match World.updateScreenTransition3 Incoming selectedScreen world with
                    | (true, world) ->
                        let eventTrace = EventTrace.debug "World" "updateScreenIncoming" "IncomingFinish" EventTrace.empty
                        let world = World.setScreenTransitionStatePlus IdlingState selectedScreen world
                        World.publish () (Events.IncomingFinish --> selectedScreen) eventTrace selectedScreen world
                    | (false, world) -> world
                | Dead -> world
            | Dead -> world

        static member private updateScreenIdling (selectedScreen : Screen) world =
            match World.getLiveness world with
            | Live ->
                match selectedScreen.GetSplashOpt world with
                | Some splash ->
                    match World.updateScreenIdling3 splash selectedScreen world with
                    | (true, world) -> World.setScreenTransitionStatePlus OutgoingState selectedScreen world
                    | (false, world) -> world
                | None ->
                    match Simulants.Game.GetDesiredScreen world with
                    | Desire desiredScreen ->
                        if desiredScreen <> selectedScreen then
                            let world = selectedScreen.SetTransitionUpdates 0L world
                            World.setScreenTransitionStatePlus OutgoingState selectedScreen world
                        else world
                    | DesireNone -> World.setScreenTransitionStatePlus OutgoingState selectedScreen world
                    | DesireIgnore -> world
            | Dead -> world

        static member private updateScreenOutgoing (selectedScreen : Screen) world =
            let world =
                if selectedScreen.GetTransitionUpdates world = 0L then
                    let incoming = selectedScreen.GetIncoming world
                    let outgoing = selectedScreen.GetOutgoing world
                    let world =
                        match outgoing.SongOpt with
                        | Some playSong ->
                            let destinationOpt =
                                match selectedScreen.GetSplashOpt world with
                                | Some splash -> Some splash.Destination
                                | None ->
                                    match World.getScreenTransitionDestinationOpt world with
                                    | Some destination -> Some destination
                                    | None ->
                                        match Simulants.Game.GetDesiredScreen world with
                                        | Desire destination -> Some destination
                                        | DesireNone -> None
                                        | DesireIgnore -> None
                            match destinationOpt with
                            | Some destination ->
                                match (incoming.SongOpt, (destination.GetIncoming world).SongOpt) with
                                | (Some song, Some song2) when assetEq song.Song song2.Song -> world // do nothing when song is the same
                                | (None, None) -> world // do nothing when neither plays a song (allowing manual control)
                                | (_, _) -> World.fadeOutSong playSong.FadeOutMs world // fade out when song is different
                            | None ->
                                match incoming.SongOpt with
                                | Some _ -> World.fadeOutSong playSong.FadeOutMs world
                                | None -> world
                        | None -> world
                    let eventTrace = EventTrace.debug "World" "updateScreenTransition" "OutgoingStart" EventTrace.empty
                    World.publish () (Events.OutgoingStart --> selectedScreen) eventTrace selectedScreen world
                else world
            match World.getLiveness world with
            | Live ->
                match World.updateScreenTransition3 Outgoing selectedScreen world with
                | (true, world) ->
                    let world = World.setScreenTransitionStatePlus IdlingState selectedScreen world
                    let world =
                        match World.getLiveness world with
                        | Live ->
                            let eventTrace = EventTrace.debug "World" "updateScreenOutgoing" "OutgoingFinish" EventTrace.empty
                            World.publish () (Events.OutgoingFinish --> selectedScreen) eventTrace selectedScreen world
                        | Dead -> world
                    match World.getLiveness world with
                    | Live ->
                        let destinationOpt =
                            match selectedScreen.GetSplashOpt world with
                            | Some splash -> Some splash.Destination
                            | None ->
                                match World.getScreenTransitionDestinationOpt world with
                                | Some destination -> Some destination
                                | None ->
                                    match Simulants.Game.GetDesiredScreen world with
                                    | Desire destination -> Some destination
                                    | DesireNone -> None
                                    | DesireIgnore -> None
                        match destinationOpt with
                        | Some destination ->
                            if destination <> selectedScreen
                            then World.selectScreen IncomingState destination world
                            else world
                        | None ->
                            let world = World.selectScreenOpt None world
                            match Simulants.Game.GetDesiredScreen world with // handle the possibility that screen deselect event changed destination
                            | Desire destination -> World.selectScreen IncomingState destination world
                            | DesireNone -> world
                            | DesireIgnore -> world
                    | Dead -> world
                | (false, world) -> world
            | Dead -> world

        static member private updateScreenTransition world =
            match World.getSelectedScreenOpt world with
            | Some selectedScreen ->
                match selectedScreen.GetTransitionState world with
                | IncomingState -> World.updateScreenIncoming selectedScreen world
                | IdlingState -> World.updateScreenIdling selectedScreen world
                | OutgoingState -> World.updateScreenOutgoing selectedScreen world
            | None ->
                match World.getDesiredScreen world with
                | Desire desiredScreen -> World.transitionScreen desiredScreen world
                | DesireNone -> world
                | DesireIgnore -> world

        /// Try to transition to the given screen if no other transition is in progress.
        [<FunctionBinding>]
        static member tryTransitionScreen destination world =
            match World.getSelectedScreenOpt world with
            | Some selectedScreen ->
                if  selectedScreen <> destination &&
                    not (World.isSelectedScreenTransitioning world) then
                    let world = World.setScreenTransitionDestinationOpt (Some destination) world |> snd'
                    let world = World.setScreenTransitionStatePlus OutgoingState selectedScreen world
                    (true, world)
                else (false, world)
            | None ->
                let world = World.setScreenTransitionStatePlus IncomingState destination world
                let world = World.setSelectedScreen destination world
                (true, world)

        /// Transition to the given screen.
        [<FunctionBinding>]
        static member transitionScreen destination world =
            World.tryTransitionScreen destination world |> snd

        /// Set the splash aspects of a screen.
        [<FunctionBinding>]
        static member setScreenSplash (splashDescriptor : SplashDescriptor) destination (screen : Screen) world =
            let splashGroup = screen / "SplashGroup"
            let splashSprite = splashGroup / "SplashSprite"
            let world = World.destroyGroupImmediate splashGroup world
            let cameraEyeSize = World.getEyeSize2d world
            let world = screen.SetSplashOpt (Some { IdlingTime = splashDescriptor.IdlingTime; Destination = destination }) world
            let world = World.createGroup<GroupDispatcher> (Some splashGroup.Name) screen world |> snd
            let world = splashGroup.SetPersistent false world
            let world = World.createEntity<StaticSpriteDispatcher> (Some splashSprite.Surnames) DefaultOverlay splashGroup world |> snd
            let world = splashSprite.SetPersistent false world
            let world = splashSprite.SetSize cameraEyeSize.V3 world
            let world = splashSprite.SetPosition (-cameraEyeSize.V3 * 0.5f) world
            let world =
                match splashDescriptor.SplashImageOpt with
                | Some splashImage ->
                    let world = splashSprite.SetStaticImage splashImage world
                    let world = splashSprite.SetVisible true world
                    world
                | None ->
                    let world = splashSprite.SetStaticImage Assets.Default.Image5 world
                    let world = splashSprite.SetVisible false world
                    world
            world

        /// Create a dissolve screen whose content is loaded from the given group file.
        [<FunctionBinding>]
        static member createDissolveScreenFromGroupFile6 dispatcherName nameOpt dissolveDescriptor songOpt groupFilePath world =
            let (dissolveScreen, world) = World.createDissolveScreen5 dispatcherName nameOpt dissolveDescriptor songOpt world
            let world = World.readGroupFromFile groupFilePath None dissolveScreen world |> snd
            (dissolveScreen, world)

        /// Create a dissolve screen whose content is loaded from the given group file.
        [<FunctionBinding>]
        static member createDissolveScreenFromGroupFile<'d when 'd :> ScreenDispatcher> nameOpt dissolveDescriptor songOpt groupFilePath world =
            World.createDissolveScreenFromGroupFile6 typeof<'d>.Name nameOpt dissolveDescriptor groupFilePath songOpt world

        /// Create a splash screen that transitions to the given destination upon completion.
        [<FunctionBinding>]
        static member createSplashScreen6 dispatcherName nameOpt splashDescriptor destination world =
            let (splashScreen, world) = World.createDissolveScreen5 dispatcherName nameOpt splashDescriptor.DissolveDescriptor None world
            let world = World.setScreenSplash splashDescriptor destination splashScreen world
            (splashScreen, world)

        /// Create a splash screen that transitions to the given destination upon completion.
        [<FunctionBinding>]
        static member createSplashScreen<'d when 'd :> ScreenDispatcher> nameOpt splashDescriptor destination world =
            World.createSplashScreen6 typeof<'d>.Name nameOpt splashDescriptor destination world

        static member internal makeIntrinsicOverlays facets entityDispatchers =
            let requiresFacetNames = fun sourceType -> sourceType = typeof<EntityDispatcher>
            let facets = facets |> Map.toValueList |> List.map box
            let entityDispatchers = entityDispatchers |> Map.toValueList |> List.map box
            let sources = facets @ entityDispatchers
            let sourceTypes = List.map (fun source -> source.GetType ()) sources
            Overlay.makeIntrinsicOverlays requiresFacetNames sourceTypes

        /// Try to reload the overlayer currently in use by the world.
        static member tryReloadOverlays inputDirectory outputDirectory world =
            
            // attempt to reload overlay file
            let inputOverlayerFilePath = inputDirectory + "/" + Assets.Global.OverlayerFilePath
            let outputOverlayerFilePath = outputDirectory + "/" + Assets.Global.OverlayerFilePath
            try File.Copy (inputOverlayerFilePath, outputOverlayerFilePath, true)

                // cache old overlayer and make new one
                let oldOverlayer = World.getOverlayer world
                let entityDispatchers = World.getEntityDispatchers world
                let facets = World.getFacets world
                let intrinsicOverlays = World.makeIntrinsicOverlays facets entityDispatchers
                match Overlayer.tryMakeFromFile intrinsicOverlays outputOverlayerFilePath with
                | Right overlayer ->

                    // update overlayer and overlay router
                    let overlays = Overlayer.getIntrinsicOverlays overlayer @ Overlayer.getExtrinsicOverlays overlayer
                    let overlayRoutes =
                        overlays |>
                        List.map (fun overlay -> overlay.OverlaidTypeNames |> List.map (fun typeName -> (typeName, overlay.OverlayName))) |>
                        List.concat
                    let overlayRouter = OverlayRouter.make overlayRoutes
                    let world = World.setOverlayer overlayer world
                    let world = World.setOverlayRouter overlayRouter world

                    // apply overlays to all entities
                    let entities = World.getEntities1 world
                    let world = Seq.fold (World.applyEntityOverlay oldOverlayer overlayer) world entities
                    (Right overlayer, world)

                // propagate errors
                | Left error -> (Left error, world)
            with exn -> (Left (scstring exn), World.choose world)

        /// Try to reload the prelude currently in use by the world.
        static member tryReloadPrelude inputDirectory outputDirectory world =
            let inputPreludeFilePath = inputDirectory + "/" + Assets.Global.PreludeFilePath
            let outputPreludeFilePath = outputDirectory + "/" + Assets.Global.PreludeFilePath
            try File.Copy (inputPreludeFilePath, outputPreludeFilePath, true)
                match World.tryEvalPrelude world with
                | Right struct (preludeStr, world) -> (Right preludeStr, world)
                | Left struct (error, world) -> (Left error, world)
            with exn -> (Left (scstring exn), World.choose world)

        /// Send a message to the subsystems to reload their existing assets.
        [<FunctionBinding>]
        static member reloadExistingAssets world =
            let world = World.reloadRenderAssets2d world
            let world = World.reloadRenderAssets3d world
            let world = World.reloadAudioAssets world
            let world = World.reloadSymbols world
            Metadata.regenerateMetadata ()
            world

        /// Attempt to reload the asset graph.
        /// Currently does not support reloading of song assets, and possibly others that are
        /// locked by the engine's subsystems.
        static member tryReloadAssetGraph inputDirectory outputDirectory refinementDirectory world =

            // attempt to reload asset graph file
            try File.Copy
                    (inputDirectory + "/" + Assets.Global.AssetGraphFilePath,
                     outputDirectory + "/" + Assets.Global.AssetGraphFilePath,
                     true)

                // attempt to load asset graph
                match AssetGraph.tryMakeFromFile (outputDirectory + "/" + Assets.Global.AssetGraphFilePath) with
                | Right assetGraph ->

                    // build assets reload asset metadata
                    AssetGraph.buildAssets inputDirectory outputDirectory refinementDirectory false assetGraph
                    Metadata.generateMetadata (World.getImperative world) assetGraph
                    let world = World.reloadExistingAssets world
                    let world = World.publish () Events.AssetsReload (EventTrace.debug "World" "publishAssetsReload" "" EventTrace.empty) Simulants.Game world
                    (Right assetGraph, world)

                // propagate errors
                | Left error -> (Left error, world)
            with exn -> (Left (scstring exn), World.choose world)

        /// Reload asset graph, build assets, then reload built assets.
        [<FunctionBinding>]
        static member tryReloadAssets world =
            let targetDir = AppDomain.CurrentDomain.BaseDirectory
            let assetSourceDir = Path.Simplify (targetDir + "../..")
            match World.tryReloadAssetGraph assetSourceDir targetDir Constants.Engine.RefinementDir world with
            | (Right _, world) -> (true, world)
            | (Left _, world) -> (false, world)

        /// Clear all messages in all subsystems.
        static member clearMessages world =
             let world = World.updatePhysicsEngine2d (fun physicsEngine -> physicsEngine.ClearMessages ()) world
             World.withRendererProcess (fun rendererProcess -> rendererProcess.ClearMessages ()) world
             World.withAudioPlayer (fun audioPlayer -> audioPlayer.ClearMessages ()) world
             world

        /// Shelve the a world for background storage.
        static member shelve world =

            // not sure if we really want to also clear physics messages here - we didn't used to
            World.clearMessages world

        /// Unshelve the state of a world.
        static member unshelve world =

            // clear existing 2d physics messages
            let world = World.updatePhysicsEngine2d (fun physicsEngine -> physicsEngine.ClearMessages ()) world

            // rebuild 2d physics state
            let world = World.enqueuePhysicsMessage2d RebuildPhysicsHackMessage world

            // propagate current physics state
            let entities = World.getEntities1 world
            let world = Seq.fold (fun world (entity : Entity) -> entity.PropagatePhysics world) world entities
            world

        static member private processTasklet simulant tasklet (taskletsNotRun : OMap<Simulant, World Tasklet UList>) world =
            let time = World.getUpdateTime world
            if time = tasklet.ScheduledTime then
                let world = tasklet.ScheduledOp world
                (taskletsNotRun, world)
            elif time > tasklet.ScheduledTime then
                Log.debug ("Tasklet leak found for time '" + scstring time + "'.")
                (taskletsNotRun, world)
            else
                let taskletsNotRun =
                    match taskletsNotRun.TryGetValue simulant with
                    | (true, taskletList) -> OMap.add simulant (UList.add tasklet taskletList) taskletsNotRun
                    | (false, _) -> OMap.add simulant (UList.singleton (OMap.getConfig taskletsNotRun) tasklet) taskletsNotRun
                (taskletsNotRun, world)

        static member private processTasklets world =
            let tasklets = World.getTasklets world
            let world = World.clearTasklets world
            let (taskletsNotRun, world) =
                OMap.fold (fun (taskletsNotRun, world) simulant taskletList ->
                    UList.fold (fun (taskletsNotRun, world) tasklet ->
                        if World.getExists simulant world
                        then World.processTasklet simulant tasklet taskletsNotRun world
                        else (taskletsNotRun, world))
                        (taskletsNotRun, world)
                        taskletList)
                    (OMap.makeEmpty HashIdentity.Structural (OMap.getConfig tasklets), world)
                    tasklets
            let taskletsNotRun = OMap.filter (fun simulant _ -> World.getExists simulant world) taskletsNotRun
            World.restoreTasklets taskletsNotRun world

        static member private destroySimulants world =
            let world =
                List.foldBack (fun simulant world ->
                    World.destroyImmediate simulant world)
                    world.WorldExtension.DestructionListRev
                    world
            if List.notEmpty world.WorldExtension.DestructionListRev
            then World.destroySimulants world
            else world

        /// Process an input event from SDL and ultimately publish any related game events.
        static member private processInput2 (evt : SDL.SDL_Event) world =
            let world =
                match evt.``type`` with
                | SDL.SDL_EventType.SDL_QUIT ->
                    World.exit world
                | SDL.SDL_EventType.SDL_MOUSEMOTION ->
                    let mousePosition = v2 (single evt.button.x) (single evt.button.y)
                    let world =
                        if World.isMouseButtonDown MouseLeft world then
                            let eventTrace = EventTrace.debug "World" "processInput" "MouseDrag" EventTrace.empty
                            World.publishPlus { MouseMoveData.Position = mousePosition } Events.MouseDrag eventTrace Simulants.Game true true world
                        else world
                    let eventTrace = EventTrace.debug "World" "processInput" "MouseMove" EventTrace.empty
                    World.publishPlus { MouseMoveData.Position = mousePosition } Events.MouseMove eventTrace Simulants.Game true true world
                | SDL.SDL_EventType.SDL_MOUSEBUTTONDOWN ->
                    let mousePosition = World.getMousePosition world
                    let mouseButton = World.toNuMouseButton (uint32 evt.button.button)
                    let mouseButtonDownEvent = stoa<MouseButtonData> ("Mouse/" + MouseButton.toEventName mouseButton + "/Down/Event")
                    let mouseButtonChangeEvent = stoa<MouseButtonData> ("Mouse/" + MouseButton.toEventName mouseButton + "/Change/Event")
                    let eventData = { Position = mousePosition; Button = mouseButton; Down = true }
                    let eventTrace = EventTrace.debug "World" "processInput" "MouseButtonDown" EventTrace.empty
                    let world = World.publishPlus eventData mouseButtonDownEvent eventTrace Simulants.Game true true world
                    let eventTrace = EventTrace.debug "World" "processInput" "MouseButtonChange" EventTrace.empty
                    World.publishPlus eventData mouseButtonChangeEvent eventTrace Simulants.Game true true world
                | SDL.SDL_EventType.SDL_MOUSEBUTTONUP ->
                    let mousePosition = World.getMousePosition world
                    let mouseButton = World.toNuMouseButton (uint32 evt.button.button)
                    let mouseButtonUpEvent = stoa<MouseButtonData> ("Mouse/" + MouseButton.toEventName mouseButton + "/Up/Event")
                    let mouseButtonChangeEvent = stoa<MouseButtonData> ("Mouse/" + MouseButton.toEventName mouseButton + "/Change/Event")
                    let eventData = { Position = mousePosition; Button = mouseButton; Down = false }
                    let eventTrace = EventTrace.debug "World" "processInput" "MouseButtonUp" EventTrace.empty
                    let world = World.publishPlus eventData mouseButtonUpEvent eventTrace Simulants.Game true true world
                    let eventTrace = EventTrace.debug "World" "processInput" "MouseButtonChange" EventTrace.empty
                    World.publishPlus eventData mouseButtonChangeEvent eventTrace Simulants.Game true true world
                | SDL.SDL_EventType.SDL_KEYDOWN ->
                    let keyboard = evt.key
                    let key = keyboard.keysym
                    let eventData = { KeyboardKey = key.scancode |> int |> enum<KeyboardKey>; Repeated = keyboard.repeat <> byte 0; Down = true }
                    let eventTrace = EventTrace.debug "World" "processInput" "KeyboardKeyDown" EventTrace.empty
                    let world = World.publishPlus eventData Events.KeyboardKeyDown eventTrace Simulants.Game true true world
                    let eventTrace = EventTrace.debug "World" "processInput" "KeyboardKeyChange" EventTrace.empty
                    World.publishPlus eventData Events.KeyboardKeyChange eventTrace Simulants.Game true true world
                | SDL.SDL_EventType.SDL_KEYUP ->
                    let keyboard = evt.key
                    let key = keyboard.keysym
                    let eventData = { KeyboardKey = key.scancode |> int |> enum<KeyboardKey>; Repeated = keyboard.repeat <> byte 0; Down = false }
                    let eventTrace = EventTrace.debug "World" "processInput" "KeyboardKeyUp" EventTrace.empty
                    let world = World.publishPlus eventData Events.KeyboardKeyUp eventTrace Simulants.Game true true world
                    let eventTrace = EventTrace.debug "World" "processInput" "KeyboardKeyChange" EventTrace.empty
                    World.publishPlus eventData Events.KeyboardKeyChange eventTrace Simulants.Game true true world
                | SDL.SDL_EventType.SDL_JOYHATMOTION ->
                    let index = evt.jhat.which
                    let direction = evt.jhat.hatValue
                    let eventData = { GamepadDirection = GamepadState.toNuDirection direction }
                    let eventTrace = EventTrace.debug "World" "processInput" "GamepadDirectionChange" EventTrace.empty
                    World.publishPlus eventData (Events.GamepadDirectionChange index) eventTrace Simulants.Game true true world
                | SDL.SDL_EventType.SDL_JOYBUTTONDOWN ->
                    let index = evt.jbutton.which
                    let button = int evt.jbutton.button
                    if GamepadState.isSdlButtonSupported button then
                        let eventData = { GamepadButton = GamepadState.toNuButton button; Down = true }
                        let eventTrace = EventTrace.debug "World" "processInput" "GamepadButtonDown" EventTrace.empty
                        let world = World.publishPlus eventData (Events.GamepadButtonDown index) eventTrace Simulants.Game true true world
                        let eventTrace = EventTrace.debug "World" "processInput" "GamepadButtonChange" EventTrace.empty
                        World.publishPlus eventData (Events.GamepadButtonChange index) eventTrace Simulants.Game true true world
                    else world
                | SDL.SDL_EventType.SDL_JOYBUTTONUP ->
                    let index = evt.jbutton.which
                    let button = int evt.jbutton.button
                    if GamepadState.isSdlButtonSupported button then
                        let eventData = { GamepadButton = GamepadState.toNuButton button; Down = true }
                        let eventTrace = EventTrace.debug "World" "processInput" "GamepadButtonUp" EventTrace.empty
                        let world = World.publishPlus eventData (Events.GamepadButtonUp index) eventTrace Simulants.Game true true world
                        let eventTrace = EventTrace.debug "World" "processInput" "GamepadButtonChange" EventTrace.empty
                        World.publishPlus eventData (Events.GamepadButtonChange index) eventTrace Simulants.Game true true world
                    else world
                | _ -> world
            (World.getLiveness world, world)

        static member private processIntegrationMessage2d integrationMessage world =
            match World.getLiveness world with
            | Live ->
                match integrationMessage with
                | BodyCollisionMessage bodyCollisionMessage ->
                    let entity = bodyCollisionMessage.BodyShapeSource.Simulant :?> Entity
                    if entity.Exists world then
                        let collisionAddress = Events.BodyCollision --> entity.EntityAddress
                        let collisionData =
                            { BodyCollider = BodyShapeSource.fromInternal bodyCollisionMessage.BodyShapeSource
                              BodyCollidee = BodyShapeSource.fromInternal bodyCollisionMessage.BodyShapeSource2
                              Normal = bodyCollisionMessage.Normal
                              Speed = bodyCollisionMessage.Speed }
                        let eventTrace = EventTrace.debug "World" "handleIntegrationMessage" "" EventTrace.empty
                        World.publish collisionData collisionAddress eventTrace Simulants.Game world
                    else world
                | BodySeparationMessage bodySeparationMessage ->
                    let entity = bodySeparationMessage.BodyShapeSource.Simulant :?> Entity
                    if entity.Exists world then
                        let separationAddress = Events.BodySeparation --> entity.EntityAddress
                        let separationData =
                            { BodySeparator = BodyShapeSource.fromInternal bodySeparationMessage.BodyShapeSource
                              BodySeparatee = BodyShapeSource.fromInternal bodySeparationMessage.BodyShapeSource2  }
                        let eventTrace = EventTrace.debug "World" "handleIntegrationMessage" "" EventTrace.empty
                        World.publish separationData separationAddress eventTrace Simulants.Game world
                    else world
                | BodyTransformMessage bodyTransformMessage ->
                    let bodySource = bodyTransformMessage.BodySource
                    let entity = bodySource.Simulant :?> Entity
                    let size = entity.GetSize world
                    let position = bodyTransformMessage.Position - size * v3UncenteredOffset
                    let rotation = bodyTransformMessage.Rotation
                    let linearVelocity = bodyTransformMessage.LinearVelocity
                    let angularVelocity = bodyTransformMessage.AngularVelocity
                    if bodySource.BodyId = 0UL
                    then entity.ApplyPhysics position rotation linearVelocity angularVelocity world
                    else world
            | Dead -> world

        static member private getEntities2dBy getElementsFromQuadtree world =
            let quadtree = World.getQuadtree world
            let (quadtree, quadtreeCache) = MutantCache.getMutant (fun () -> World.rebuildQuadtree world) quadtree
            let world = World.setQuadtree quadtreeCache world
            let entities : Entity seq = getElementsFromQuadtree quadtree
            (entities, world)

        /// Get all uncullable (non-cullable) 2d entities.
        static member getEntitiesOmnipresent2d set world =
            World.getEntities2dBy (Quadtree.getElementsOmnipresent set) world

        /// Get all 2d entities in the given bounds, including all uncullable entities.
        static member getEntitiesInBounds2d bounds set world =
            World.getEntities2dBy (Quadtree.getElementsInBounds bounds set) world

        /// Get all 2d entities at the given point, including all uncullable entities.
        static member getEntitiesAtPoint2d point set world =
            World.getEntities2dBy (Quadtree.getElementsAtPoint point set) world

        /// Get all 2d entities needing to update for the current 2d play zone, including all uncullable entities.
        static member getEntitiesInPlay2d set world =
            let playBounds = World.getPlayBounds2d world
            World.getEntities2dBy (Quadtree.getElementsInBounds playBounds set) world

        /// Get all 2d entities in the current 2d view, including all uncullable entities.
        static member getEntitiesInView2d set world =
            let viewBounds = World.getViewBounds2d world
            World.getEntities2dBy (Quadtree.getElementsInBounds viewBounds set) world

        static member private getEntities3dBy getElementsFromOctree world =
            let octree = World.getOctree world
            let (octree, octreeCache) = MutantCache.getMutant (fun () -> World.rebuildOctree world) octree
            let world = World.setOctree octreeCache world
            let elements = getElementsFromOctree octree
            let entities = Seq.map (fun element -> element.Entry) elements
            (entities, world)

        /// Get all uncullable 3d entities.
        static member getEntitiesOmnipresent3d set world =
            World.getEntities3dBy (Octree.getElementsOmnipresent set) world

        /// Get all 3d entities in the given bounds, including all uncullable entities.
        static member getEntitiesInBounds3d bounds set world =
            World.getEntities3dBy (Octree.getElementsInBounds bounds set) world

        /// Get all 3d entities at the given point, including all uncullable entities.
        static member getEntitiesAtPoint3d point set world =
            World.getEntities3dBy (Octree.getElementsAtPoint point set) world

        /// Get all 3d entities in the current 3d play zone, including all uncullable entities.
        static member getEntitiesInPlay3d set world =
            let struct (playBox, playFrustum) = World.getPlayBounds3d world
            World.getEntities3dBy (Octree.getElementsInPlay playBox playFrustum set) world

        /// Get all 3d entities in the current 3d view, including all uncullable entities.
        static member getEntitiesInView3d set world =
            let frustumEnclosed = World.getEyeFrustum3dEnclosed world
            let frustumExposed = World.getEyeFrustum3dExposed world
            let frustumImposter = World.getEyeFrustum3dImposter world
            let lightBox = World.getLightBox3d world
            World.getEntities3dBy (Octree.getElementsInView frustumEnclosed frustumExposed frustumImposter lightBox set) world

        static member private updateSimulants world =

            // gather simulants
            UpdateGatherTimer.Start ()
            let screens = match World.getOmniScreenOpt world with Some omniScreen -> [omniScreen] | None -> []
            let screens = match World.getSelectedScreenOpt world with Some selectedScreen -> selectedScreen :: screens | None -> screens
            let screens = List.rev screens
            let groups = Seq.concat (List.map (flip World.getGroups world) screens)
            let (entities3d, world) = World.getEntitiesInPlay3d CachedHashSet3d world
            let (entities2d, world) = World.getEntitiesInPlay2d CachedHashSet2d world
            let entities = Seq.append entities3d entities2d
            UpdateGatherTimer.Stop ()

            // update game
            UpdateGameTimer.Start ()
            let world = World.updateGame world
            UpdateGameTimer.Stop ()
            
            // update screens
            UpdateScreensTimer.Start ()
            let world = List.fold (fun world screen -> World.updateScreen screen world) world screens
            UpdateScreensTimer.Stop ()

            // update groups
            UpdateGroupsTimer.Start ()
            let world = Seq.fold (fun world group -> World.updateGroup group world) world groups
            UpdateGroupsTimer.Stop ()

            // update entities
            UpdateEntitiesTimer.Start ()
            let advancing = World.getAdvancing world
            let world =
                Seq.fold (fun world (entity : Entity) ->
                    if not (entity.GetStatic world) && (entity.GetAlwaysUpdate world || advancing)
                    then World.updateEntity entity world
                    else world)
                    world
                    entities
            UpdateEntitiesTimer.Stop ()

            // clear cached hash sets
            CachedHashSet3d.Clear ()
            CachedHashSet2d.Clear ()

            // fin
            world

        static member private postUpdateSimulants world =

            // gather simulants
            PostUpdateGatherTimer.Start ()
            let screens = match World.getOmniScreenOpt world with Some omniScreen -> [omniScreen] | None -> []
            let screens = match World.getSelectedScreenOpt world with Some selectedScreen -> selectedScreen :: screens | None -> screens
            let screens = List.rev screens
            let groups = Seq.concat (List.map (flip World.getGroups world) screens)
#if !DISABLE_ENTITY_POST_UPDATE
            let (entities3d, world) = World.getEntitiesInPlay3d CachedHashSet3d world
            let (entities2d, world) = World.getEntitiesInPlay2d CachedHashSet2d world
            let entities = Seq.append entities3d entities2d
#endif
            PostUpdateGatherTimer.Stop ()

            // post-update game
            PostUpdateGameTimer.Start ()
            let world = World.postUpdateGame world
            PostUpdateGameTimer.Stop ()

            // post-update screens
            PostUpdateScreensTimer.Start ()
            let world = List.fold (fun world screen -> World.postUpdateScreen screen world) world screens
            PostUpdateScreensTimer.Stop ()

            // post-update groups
            PostUpdateGroupsTimer.Start ()
            let world = Seq.fold (fun world group -> World.postUpdateGroup group world) world groups
            PostUpdateGroupsTimer.Stop ()

#if !DISABLE_ENTITY_POST_UPDATE
            // post-update entities
            PostUpdateEntitiesTimer.Start ()
            let advancing = World.getAdvancing world
            let world =
                Seq.fold (fun world (entity : Entity) ->
                    if not (entity.GetStatic world) && (entity.GetAlwaysUpdate world || advancing)
                    then World.postUpdateEntity entity world
                    else world)
                    world
                    entities
            PostUpdateEntitiesTimer.Stop ()

            // clear cached hash sets
            CachedHashSet3d.Clear ()
            CachedHashSet2d.Clear ()
#endif

            // fin
            world

        static member private renderScreenTransition5 (_ : Vector2) (eyeSize : Vector2) (screen : Screen) transition world =
            match transition.DissolveImageOpt with
            | Some dissolveImage ->
                let progress = single (screen.GetTransitionUpdates world) / single (inc transition.TransitionLifeTime)
                let alpha = match transition.TransitionType with Incoming -> 1.0f - progress | Outgoing -> progress
                let color = Color.One.WithA alpha
                let position = -eyeSize.V3 * 0.5f
                let size = eyeSize.V3
                let mutable transform = Transform.makeDefault false
                transform.Position <- position
                transform.Size <- size
                transform.Elevation <- Single.MaxValue
                transform.Absolute <- true
                World.enqueueRenderLayeredMessage2d
                    { Elevation = transform.Elevation
                      Horizon = transform.Perimeter.Position.Y
                      AssetTag = AssetTag.generalize dissolveImage
                      RenderDescriptor2d =
                        SpriteDescriptor
                            { Transform = transform
                              InsetOpt = ValueNone
                              Image = dissolveImage
                              Color = color
                              Blend = Transparent
                              Glow = Color.Zero
                              Flip = FlipNone }}
                    world
            | None -> world

        static member private renderScreenTransition (screen : Screen) world =
            match screen.GetTransitionState world with
            | IncomingState -> World.renderScreenTransition5 (World.getEyePosition2d world) (World.getEyeSize2d world) screen (screen.GetIncoming world) world
            | OutgoingState -> World.renderScreenTransition5 (World.getEyePosition2d world) (World.getEyeSize2d world) screen (screen.GetOutgoing world) world
            | IdlingState -> world

        static member private renderSimulants world =

            // gather simulants
            RenderGatherTimer.Start ()
            let screens = match World.getOmniScreenOpt world with Some omniScreen -> [omniScreen] | None -> []
            let screens = match World.getSelectedScreenOpt world with Some selectedScreen -> selectedScreen :: screens | None -> screens
            let screens = List.rev screens
            let groups = Seq.concat (List.map (flip World.getGroups world) screens)
            let (entities3d, world) = World.getEntitiesInView3d CachedHashSet3d world
            let (entities2d, world) = World.getEntitiesInView2d CachedHashSet2d world
            let entities = Seq.append entities3d entities2d
            RenderGatherTimer.Stop ()

            // render simulants breadth-first
            let world = World.renderGame world
            let world = List.fold (fun world screen -> World.renderScreen screen world) world screens
            let world = match World.getSelectedScreenOpt world with Some selectedScreen -> World.renderScreenTransition selectedScreen world | None -> world
            let world = Seq.fold (fun world (group : Group) -> if group.GetVisible world then World.renderGroup group world else world) world groups

            // render entities
            RenderEntitiesTimer.Start ()
            let world =
                if World.getStandAlone world then
                    Seq.fold (fun world (entity : Entity) ->
                        if entity.GetVisible world
                        then World.renderEntity entity world
                        else world)
                        world entities
                else
                    Seq.fold (fun world (entity : Entity) ->
                        let group = entity.Group
                        if group.GetVisible world && entity.GetVisible world
                        then World.renderEntity entity world
                        else world)
                        world entities
            RenderEntitiesTimer.Stop ()

            // clear cached hash sets
            CachedHashSet3d.Clear ()
            CachedHashSet2d.Clear ()

            // fin
            world

        static member private processInput world =
            if SDL.SDL_WasInit SDL.SDL_INIT_TIMER <> 0u then
                let mutable result = (Live, world)
                let mutable polledEvent = SDL.SDL_Event ()
                while
                    SDL.SDL_PollEvent &polledEvent <> 0 &&
                    (match fst result with Live -> true | Dead -> false) do
                    result <- World.processInput2 polledEvent (snd result)
                result
            else (Dead, world)

        static member private processPhysics world =
            let physicsEngine = World.getPhysicsEngine2d world
            let (physicsMessages, physicsEngine) = physicsEngine.PopMessages ()
            let world = World.setPhysicsEngine2d physicsEngine world
            let integrationMessages = physicsEngine.Integrate (World.getUpdateRate world) physicsMessages
            let world = Seq.fold (flip World.processIntegrationMessage2d) world integrationMessages
            world

        static member private cleanUp world =
            let world = World.unregisterGame world
            World.cleanUpSubsystems world |> ignore

        /// Run the game engine with threading with the given handlers, but don't clean up at the end, and return the world.
        static member runWithoutCleanUp runWhile preProcess perProcess postProcess (sdlDeps : SdlDeps) liveness firstFrame world =
            TotalTimer.Start ()
            if runWhile world then
                if World.shouldSleep world then Thread.Sleep (1000 / Constants.Engine.DesiredFpsI) // don't let game run too fast while full screen unfocused
                PreProcessTimer.Start ()
                let world = preProcess world
                PreProcessTimer.Stop ()
                match liveness with
                | Live ->                
                    let world = World.updateScreenTransition world
                    match World.getLiveness world with
                    | Live ->
                        InputTimer.Start ()
                        let (liveness, world) = World.processInput world
                        InputTimer.Stop ()
                        match liveness with
                        | Live ->
                            PhysicsTimer.Start ()
                            let world = World.processPhysics world
                            PhysicsTimer.Stop ()
                            match World.getLiveness world with
                            | Live ->
                                UpdateTimer.Start ()
                                let world = World.updateSimulants world
                                UpdateTimer.Stop ()
                                match World.getLiveness world with
                                | Live ->
                                    PostUpdateTimer.Start ()
                                    let world = World.postUpdateSimulants world
                                    PostUpdateTimer.Stop ()
                                    match World.getLiveness world with
                                    | Live ->
                                        PerProcessTimer.Start ()
                                        let world = perProcess world
                                        PerProcessTimer.Stop ()
                                        match World.getLiveness world with
                                        | Live ->
                                            TaskletsTimer.Start ()
                                            WorldModule.TaskletProcessingStarted <- true
                                            let world = World.processTasklets world
                                            TaskletsTimer.Stop ()
                                            match World.getLiveness world with
                                            | Live ->
                                                DestructionTimer.Start ()
                                                let world = World.destroySimulants world
                                                DestructionTimer.Stop ()
                                                match World.getLiveness world with
                                                | Live ->
                                                    PostProcessTimer.Start ()
                                                    let world = World.postProcess world
                                                    let world = postProcess world
                                                    PostProcessTimer.Stop ()
                                                    match World.getLiveness world with
                                                    | Live ->
                                                        RenderTimer.Start ()
                                                        let world = World.renderSimulants world
                                                        RenderTimer.Stop ()
                                                        match World.getLiveness world with
                                                        | Live ->

                                                            // process audio
                                                            AudioTimer.Start ()
                                                            let world =
                                                                if SDL.SDL_WasInit SDL.SDL_INIT_AUDIO <> 0u then
                                                                    let audioPlayer = World.getAudioPlayer world
                                                                    let audioMessages = audioPlayer.PopMessages ()
                                                                    audioPlayer.Play audioMessages
                                                                    world
                                                                else world
                                                            AudioTimer.Stop ()

                                                            // process rendering
                                                            let rendererProcess = World.getRendererProcess world
                                                            if not firstFrame then rendererProcess.Swap ()
                                                            rendererProcess.SubmitMessages
                                                                (World.getEyePosition2d world)
                                                                (World.getEyeSize2d world)
                                                                (World.getEyePosition3d world)
                                                                (World.getEyeRotation3d world)
                                                                (World.getWindowSize world)

                                                            // update counters and recur
                                                            TotalTimer.Stop ()
                                                            let world = World.updateTime world
                                                            WorldModule.TaskletProcessingStarted <- false
                                                            World.runWithoutCleanUp runWhile preProcess perProcess postProcess sdlDeps liveness false world

                                                        | Dead -> world
                                                    | Dead -> world
                                                | Dead -> world
                                            | Dead -> world
                                        | Dead -> world
                                    | Dead -> world
                                | Dead -> world
                            | Dead -> world
                        | Dead -> world
                    | Dead -> world
                | Dead -> world
            else world

        /// Run the game engine with the given handler.
        static member run4 runWhile (sdlDeps : SdlDeps) liveness world =
            let result =
                try let world = World.runWithoutCleanUp runWhile id id id sdlDeps liveness true world
                    World.cleanUp world
                    Constants.Engine.SuccessExitCode
                with exn ->
                    let world = World.choose world
                    Log.trace (scstring exn)
                    World.cleanUp world
                    Constants.Engine.FailureExitCode
#if MULTITHREAD_RUN_LOOP
            // stops background threads
            Environment.Exit result
#endif
            result

[<AutoOpen>]
module GameDispatcherModule =

    type World with

        static member internal signalGame<'model, 'message, 'command> signal (game : Game) world =
            match game.GetDispatcher world with
            | :? GameDispatcher<'model, 'message, 'command> as dispatcher ->
                Signal.processSignal dispatcher.Message dispatcher.Command (game.ModelGeneric<'model> ()) signal game world
            | _ -> Log.info "Failed to send signal to game."; world

    and Game with

        member this.UpdateModel<'model> updater world =
            this.SetModelGeneric<'model> (updater (this.GetModelGeneric<'model> world)) world

        member this.Signal<'model, 'message, 'command> signal world =
            World.signalGame<'model, 'message, 'command> signal this world

    and [<AbstractClass>] GameDispatcher<'model, 'message, 'command> (makeInitial : unit -> 'model) =
        inherit GameDispatcher ()

        new (initial : 'model) =
            GameDispatcher<'model, 'message, 'command> (fun () -> initial)

        member this.GetModel (game : Game) world : 'model =
            game.GetModelGeneric<'model> world

        member this.SetModel (model : 'model) (game : Game) world =
            game.SetModelGeneric<'model> model world

        member this.Model (game : Game) =
            lens (nameof this.Model) (this.GetModel game) (flip this.SetModel game) game

        override this.Register (game, world) =
            let world =
                let property = World.getGameModelProperty world
                if property.DesignerType = typeof<unit>
                then game.SetModelGeneric<'model> (makeInitial ()) world
                else world
            let channels = this.Channel (this.Model game, game)
            let world = Signal.processChannels this.Message this.Command (this.Model game) channels game world
            let content = this.Content (this.Model game, game)
            let (screensRev, world) =
                List.fold (fun (screensRev, world) content ->
                    let (screen, world) = World.expandScreenContent World.setScreenSplash content (SimulantOrigin game) game world
                    (screen :: screensRev, world))
                    ([], world) content
            let screens = List.rev screensRev
            let world =
                match screens with
                | [] -> game.SetDesiredScreen DesireNone world
                | screen :: _ -> game.SetDesiredScreen (Desire screen) world
            let initializers = this.Initializers (this.Model game, game)
            List.fold (fun world initializer ->
                match initializer with
                | PropertyDefinition def ->
                    let property = { PropertyType = def.PropertyType; PropertyValue = PropertyExpr.eval def.PropertyExpr world }
                    World.setProperty def.PropertyName property game world |> snd'
                | EventHandlerDefinition (handler, partialAddress) ->
                    let eventAddress = partialAddress --> game
                    World.monitor (fun (evt : Event) world ->
                        let world = WorldModule.trySignal (handler evt) game world
                        (Cascade, world))
                        eventAddress (game :> Simulant) world
                | BindDefinition (left, right) ->
                    WorldModule.bind5 true game left right world
                | LinkDefinition (left, right) ->
                    let world = WorldModule.bind5 false game left right world
                    WorldModule.bind5 false right.This right left world)
                world initializers

        override this.Render (game, world) =
            let view = this.View (this.GetModel game world, game, world)
            World.renderView view world

        override this.TrySignal (signalObj, game, world) =
            match signalObj with
            | :? Signal<'message, obj> as signal -> game.Signal<'model, 'message, 'command> (match signal with Message message -> msg message | _ -> failwithumf ()) world
            | :? Signal<obj, 'command> as signal -> game.Signal<'model, 'message, 'command> (match signal with Command command -> cmd command | _ -> failwithumf ()) world
            | _ -> Log.info "Incorrect signal type returned from event binding."; world

        abstract member Channel : Lens<'model, World> * Game -> Channel<'message, 'command, Game, World> list
        default this.Channel (_, _) = []

        abstract member Initializers : Lens<'model, World> * Game -> PropertyInitializer list
        default this.Initializers (_, _) = []

        abstract member Message : 'model * 'message * Game * World -> Signal<'message, 'command> list * 'model
        default this.Message (model, _, _, _) = just model

        abstract member Command : 'model * 'command * Game * World -> Signal<'message, 'command> list * World
        default this.Command (_, _, _, world) = just world

        abstract member Content : Lens<'model, World> * Game -> ScreenContent list
        default this.Content (_, _) = []

        abstract member View : 'model * Game * World -> View
        default this.View (_, _, _) = View.empty

[<AutoOpen>]
module WorldModule2' =

    type World with

        /// Attempt to send a signal to a facet.
        static member trySignalFacet signal facetName (simulant : Simulant) world =
            match simulant with
            | :? Entity as entity -> entity.TrySignalEntityFacet signal facetName world
            | _ -> failwithumf ()

        /// Send a signal to a simulant.
        static member trySignal signal (simulant : Simulant) world =
            match simulant with
            | :? Entity as entity -> entity.TrySignal signal world
            | :? Group as group -> group.TrySignal signal world
            | :? Screen as screen -> screen.TrySignal signal world
            | :? Game as game -> game.TrySignal signal world
            | _ -> failwithumf ()

        /// Send a signal to a facet.
        static member signalFacet<'model, 'message, 'command> signal facetName (simulant : Simulant) world =
            match simulant with
            | :? Entity as entity -> entity.SignalEntityFacet<'model, 'message, 'command> signal facetName world
            | _ -> failwithumf ()

        /// Send a signal to a simulant.
        static member signal<'model, 'message, 'command> signal (simulant : Simulant) world =
            match simulant with
            | :? Entity as entity -> entity.Signal<'model, 'message, 'command> signal world
            | :? Group as group -> group.Signal<'model, 'message, 'command> signal world
            | :? Screen as screen -> screen.Signal<'model, 'message, 'command> signal world
            | :? Game as game -> game.Signal<'model, 'message, 'command> signal world
            | _ -> failwithumf ()