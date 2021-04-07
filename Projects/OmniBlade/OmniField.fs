﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2020.

namespace OmniBlade
open System
open System.IO
open System.Numerics
open FSharpx.Collections
open Prime
open Nu

type [<ReferenceEquality; NoComparison>] FieldTransition =
    { FieldType : FieldType
      FieldDestination : Vector2
      FieldDirection : Direction
      FieldTransitionTime : int64 }

[<RequireQualifiedAccess>]
module Field =

    type [<ReferenceEquality; NoComparison>] Field =
        private
            { FieldType_ : FieldType
              OmniSeedState_ : OmniSeedState
              Avatar_ : Avatar
              Team_ : Map<int, Teammate>
              SpiritActivity_ : single
              Spirits_ : Spirit array
              Advents_ : Advent Set
              PropStates_ : Map<int, PropState>
              Inventory_ : Inventory
              Menu_ : Menu
              Cue_ : Cue
              ShopOpt_ : Shop option
              FieldTransitionOpt_ : FieldTransition option
              DialogOpt_ : Dialog option
              BattleOpt_ : Battle option }

        (* Local Properties *)
        member this.FieldType = this.FieldType_
        member this.OmniSeedState = this.OmniSeedState_
        member this.Avatar = this.Avatar_
        member this.Team = this.Team_
        member this.SpiritActivity = this.SpiritActivity_
        member this.Spirits = this.Spirits_
        member this.Advents = this.Advents_
        member this.PropStates = this.PropStates_
        member this.Inventory = this.Inventory_
        member this.Menu = this.Menu_
        member this.Cue = this.Cue_
        member this.ShopOpt = this.ShopOpt_
        member this.FieldTransitionOpt = this.FieldTransitionOpt_
        member this.DialogOpt = this.DialogOpt_
        member this.BattleOpt = this.BattleOpt_

    let getRecruitmentFee (field : Field) =
        let advents = Set.ofArray [|GarrouRecruited; MaelRecruited; RiainRecruited; PericRecruited|]
        let recruiteds = Set.intersect advents field.Advents
        let recruited = Set.count recruiteds
        match Array.tryItem recruited Constants.Field.RecruitmentFees with
        | Some recruitmentFee -> recruitmentFee
        | None -> 0

    let getParty field =
        field.Team_ |>
        Map.filter (fun _ teammate -> Option.isSome teammate.PartyIndexOpt) |>
        Map.toSeq |>
        Seq.tryTake 3 |>
        Map.ofSeq

    let getFieldSongOpt field =
        match Data.Value.Fields.TryGetValue field.FieldType_ with
        | (true, fieldData) -> fieldData.FieldSongOpt
        | (false, _) -> None

    let updateFieldType updater field =
        { field with
            FieldType_ = updater field.FieldType_
            SpiritActivity_ = 0.0f }

    let updateAvatar updater field =
        { field with Avatar_ = updater field.Avatar_ }

    let updateTeam updater field =
        { field with Team_ = updater field.Team_ }

    let updateAdvents updater field =
        { field with Advents_ = updater field.Advents_ }

    let updatePropStates updater field =
        { field with PropStates_ = updater field.PropStates_ }

    let updateInventory updater field =
        { field with Inventory_ = updater field.Inventory_ }

    let updateMenu updater field =
        { field with Menu_ = updater field.Menu_ }

    let updateCue updater field =
        { field with Cue_ = updater field.Cue_ }

    let updateShopOpt updater field =
        { field with ShopOpt_ = updater field.ShopOpt_ }

    let updateDialogOpt updater field =
        { field with DialogOpt_ = updater field.DialogOpt_ }

    let updateFieldTransitionOpt updater field =
        { field with FieldTransitionOpt_ = updater field.FieldTransitionOpt_ }

    let updateBattleOpt updater field =
        let battleOpt = updater field.BattleOpt_
        { field with
            BattleOpt_ = battleOpt
            SpiritActivity_ = if Option.isSome battleOpt then 0.0f else field.SpiritActivity_ }

    let updateReference field =
        { field with FieldType_ = field.FieldType_ }

    let recruit allyType (field : Field) =
        let index = Map.count field.Team
        let teammate = Teammate.make index allyType
        updateTeam (Map.add index teammate) field

    let restoreTeam field =
        { field with Team_ = Map.map (fun _ -> Teammate.restore) field.Team_ }

    let hasEncounters (field : Field) =
        match Data.Value.Fields.TryGetValue field.FieldType with
        | (true, fieldData) -> Option.isSome fieldData.EncounterTypeOpt
        | (false, _) -> false

    let advanceSpirits (field : Field) world =
        match field.FieldTransitionOpt with
        | None ->
            let field =
                { field with
                    SpiritActivity_ = inc field.SpiritActivity_ }
            let field =
                { field with
                    Spirits_ =
                        Array.map (Spirit.advance (World.getTickTime world) field.Avatar.Center) field.Spirits_ }
            let field =
                { field with
                    Spirits_ =
                        Array.filter (fun (spirit : Spirit) ->
                            let delta = field.Avatar.Bottom - spirit.Center
                            let distance = delta.Length ()
                            distance < Constants.Field.SpiritRadius * 1.25f)
                            field.Spirits }
            let field =
                let spiritActivity = max 0.0f (field.SpiritActivity_  - single Constants.Field.SpiritActivityMinimum)
                let spiritsNeeded = int (spiritActivity / single Constants.Field.SpiritActivityThreshold)
                let spiritsDeficient = spiritsNeeded - Array.length field.Spirits
                let spiritsSpawned =
                    match Data.Value.Fields.TryGetValue field.FieldType with
                    | (true, fieldData) ->
                        [|0 .. spiritsDeficient - 1|] |>
                        Array.map (fun _ ->
                            match FieldData.tryGetSpiritType field.OmniSeedState field.Avatar.Bottom fieldData world with
                            | Some spiritType ->
                                let spiritMovement = SpiritPattern.toSpiritMovement (SpiritPattern.random ())
                                let spirit = Spirit.spawn (World.getTickTime world) field.Avatar.Bottom spiritType spiritMovement
                                Some spirit
                            | None -> None) |>
                        Array.definitize
                    | (false, _) -> [||]
                { field with Spirits_ = Array.append field.Spirits_ spiritsSpawned }
            match Array.tryFind (fun (spirit : Spirit) -> Math.isPointInBounds spirit.Position field.Avatar.LowerBounds) field.Spirits_ with
            | Some spirit ->
                match Data.Value.Fields.TryGetValue field.FieldType with
                | (true, fieldData) ->
                    match fieldData.EncounterTypeOpt with
                    | Some encounterType ->
                        match Data.Value.Encounters.TryGetValue encounterType with
                        | (true, encounterData) ->
                            let battleType =
                                // TODO: P1: toughen up this code.
                                match spirit.SpiritType with
                                | WeakSpirit -> encounterData.BattleTypes.[0]
                                | NormalSpirit -> encounterData.BattleTypes.[1]
                                | StrongSpirit -> encounterData.BattleTypes.[2]
                                | GreatSpirit -> encounterData.BattleTypes.[4]
                            match Data.Value.Battles.TryGetValue battleType with
                            | (true, battleData) ->
                                let field = { field with Spirits_ = [||] }
                                Left (battleData, field)
                            | (false, _) -> Right field
                        | (false, _) -> Right field
                    | None -> Right field
                | (false, _) -> Right field
            | None -> Right field
        | Some _ -> Right field

    let synchronizeTeamFromAllies allies field =
        Map.foldi (fun i field _ (ally : Character) ->
            updateTeam (fun team ->
                match Map.tryFind i team with
                | Some teammate ->
                    let teammate =
                        { teammate with
                            HitPoints = ally.HitPoints
                            TechPoints = ally.TechPoints
                            ExpPoints = ally.ExpPoints }
                    Map.add i teammate team
                | None -> team)
                field)
            field
            allies

    let synchronizeFromBattle consequents battle field =
        let allies = Battle.getAllies battle
        let field = synchronizeTeamFromAllies allies field
        let field = updateInventory (constant battle.Inventory) field
        let field = updateBattleOpt (constant None) field
        let field = updateAdvents (Set.union consequents) field
        field

    let toSymbolizable field =
        { field with Avatar_ = Avatar.toSymbolizable field.Avatar }

    let make fieldType randSeedState avatar team advents inventory =
        { FieldType_ = fieldType
          OmniSeedState_ = OmniSeedState.makeFromSeedState randSeedState
          Avatar_ = avatar
          SpiritActivity_ = 0.0f
          Spirits_ = [||]
          Team_ = team
          Advents_ = advents
          PropStates_ = Map.empty
          Inventory_ = inventory
          Menu_ = { MenuState = MenuClosed; MenuUseOpt = None }
          Cue_ = Cue.Nil
          ShopOpt_ = None
          FieldTransitionOpt_ = None
          DialogOpt_ = None
          BattleOpt_ = None }

    let empty =
        { FieldType_ = DebugRoom
          OmniSeedState_ = OmniSeedState.make ()
          Avatar_ = Avatar.empty
          Team_ = Map.empty
          SpiritActivity_ = 0.0f
          Spirits_ = [||]
          Advents_ = Set.empty
          PropStates_ = Map.empty
          Inventory_ = { Items = Map.empty; Gold = 0 }
          Menu_ = { MenuState = MenuClosed; MenuUseOpt = None }
          Cue_ = Cue.Nil
          ShopOpt_ = None
          FieldTransitionOpt_ = None
          DialogOpt_ = None
          BattleOpt_ = None }

    let debug =
        { empty with
            Team_ = Map.singleton 0 (Teammate.make 0 Jinn) }

    let initial randSeedState =
        { empty with
            FieldType_ = TombOuter
            OmniSeedState_ = OmniSeedState.makeFromSeedState randSeedState
            Avatar_ = Avatar.initial
            Team_ = Map.singleton 0 (Teammate.make 0 Jinn)
            Inventory_ = Inventory.initial }

    let save field =
        let fieldSymbolizable = toSymbolizable field
        let fileStr = scstring fieldSymbolizable
        try File.WriteAllText (Assets.Global.SaveFilePath, fileStr) with _ -> ()

    let loadOrInitial randSeedState =
        try let fieldStr = File.ReadAllText Assets.Global.SaveFilePath
            scvalue<Field> fieldStr
        with _ -> initial randSeedState

type Field = Field.Field