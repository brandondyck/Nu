﻿namespace Elmario
open Prime
open Nu
open Nu.Declarative

[<RequireQualifiedAccess>]
module Simulants =

    // here we create an entity reference for Elmario. This is useful for simulants that you want
    // to refer to from multiple places
    let Elmario = Simulants.Default.Group / "Elmario"

// this is our Elm-style command type
type Command =
    | Jump
    | MoveLeft
    | MoveRight
    | Nop

// this is our Elm-style game dispatcher
type ElmarioDispatcher () =
    inherit GameDispatcher<unit, unit, Command> (())

    // here we channel events to signals
    override this.Channel (_, game) =
        [game.KeyboardKeyDownEvent =|> fun evt ->
            if evt.Data.KeyboardKey = KeyboardKey.Up && not evt.Data.Repeated then cmd Jump
            else cmd Nop
         game.UpdateEvent =|> fun _ ->
            if KeyboardState.isKeyDown KeyboardKey.Left then cmd MoveLeft
            elif KeyboardState.isKeyDown KeyboardKey.Right then cmd MoveRight
            else cmd Nop]

    // here we handle the Elm-style commands
    override this.Command (_, command, _, world) =
        let world =
            match command with
            | Jump ->
                let physicsId = Simulants.Elmario.GetPhysicsId world
                if World.isBodyOnGround physicsId world then
                    let world = World.playSound Constants.Audio.SoundVolumeDefault (asset "Gameplay" "Jump") world
                    World.applyBodyForce (v3 0.0f 140000.0f 0.0f) physicsId world
                else world
            | MoveLeft ->
                let physicsId = Simulants.Elmario.GetPhysicsId world
                if World.isBodyOnGround physicsId world
                then World.applyBodyForce (v3 -2500.0f 0.0f 0.0f) physicsId world
                else World.applyBodyForce (v3 -750.0f 0.0f 0.0f) physicsId world
            | MoveRight ->
                let physicsId = Simulants.Elmario.GetPhysicsId world
                if World.isBodyOnGround physicsId world
                then World.applyBodyForce (v3 2500.0f 0.0f 0.0f) physicsId world
                else World.applyBodyForce (v3 750.0f 0.0f 0.0f) physicsId world
            | Nop -> world
        just world

    // here we describe the content of the game including elmario, the ground he walks on, and a rock.
    override this.Content (_, _) =
        [Content.screen Simulants.Default.Screen.Name Vanilla []
            [Content.group Simulants.Default.Group.Name []
                [Content.sideViewCharacter Simulants.Elmario.Name
                    [Entity.Position == v3 0.0f 0.0f 0.0f
                     Entity.Size == v3 108.0f 108.0f 0.0f]
                 Content.block2d "Ground"
                    [Entity.Position == v3 -384.0f -256.0f 0.0f
                     Entity.Size == v3 768.0f 64.0f 0.0f
                     Entity.StaticImage == asset "Gameplay" "TreeTop"]
                 Content.block2d "Rock"
                    [Entity.Position == v3 320.0f -192.0f 0.0f
                     Entity.Size == v3 64.0f 64.0f 0.0f
                     Entity.StaticImage == asset "Gameplay" "Rock"]]]]