﻿// Nu Game Engine.
// Copyright (C) Bryan Edds, 2013-2020.

namespace OmniBlade
open System
open System.Numerics
open FSharpx.Collections
open Prime
open Nu
open Nu.Declarative

[<RequireQualifiedAccess>]
module Content =

    let private pageItems5 pageSize pageIndex filter sort (items : Map<ItemType, int>) =
        let items =
            items |>
            Map.toSeq |>
            (fun items -> if filter then ItemType.filterSellableItems items else items) |>
            (fun items -> if sort then ItemType.sortItems items else items)
        let itemsPaged =
            items |>
            Seq.index |>
            Seq.chunkBySize pageSize |>
            Seq.trySkip pageIndex |>
            Seq.map List.ofArray |>
            Seq.tryHead |>
            Option.defaultValue [] |>
            Seq.indexed |>
            Map.ofSeq |>
            Map.map (fun _ (i, (item, count)) -> (i, (item, Some count)))
        let pageUp =
            if itemsPaged.Count <> 0 then
                let firstItemPaged = Seq.head itemsPaged
                fst (snd firstItemPaged.Value) <> fst (Seq.head items)
            else false
        let pageDown =
            if itemsPaged.Count <> 0 then
                let lastItemPaged = Seq.last itemsPaged
                fst (snd lastItemPaged.Value) <> fst (Seq.last items)
            else false
        (pageUp, pageDown, itemsPaged)

    let pageItems rows (field : Field) =
        match field.Menu.MenuState with
        | MenuItem menu -> pageItems5 rows menu.ItemPage false true field.Inventory.Items
        | _ ->
            match field.ShopOpt with
            | Some shop ->
                match shop.ShopState with
                | ShopBuying ->
                    match Map.tryFind shop.ShopType Data.Value.Shops with
                    | Some shopData -> pageItems5 rows shop.ShopPage false false (Map.ofListBy (flip Pair.make 1) shopData.ShopItems)
                    | None -> (false, false, Map.empty)
                | ShopSelling -> pageItems5 rows shop.ShopPage true true field.Inventory.Items
            | None -> (false, false, Map.empty)

    let sidebar name position elevation (field : Lens<Field, World>) menuTeamOpen menuItemsOpen menuTechOpen menuOptionsOpen menuClose =
        Content.association name []
            [Content.button "TeamButton"
                [Entity.PositionLocal == position; Entity.ElevationLocal == elevation; Entity.Size == v3 72.0f 72.0f 0.0f
                 Entity.UpImage == asset "Field" "TeamButtonUp"
                 Entity.DownImage == asset "Field" "TeamButtonDown"
                 Entity.EnabledLocal <== field --> fun field -> match field.Menu.MenuState with MenuTeam _ -> false | _ -> true
                 Entity.ClickEvent ==> msg (menuTeamOpen ())]
             Content.button "InventoryButton"
                [Entity.PositionLocal == position - v3 0.0f 81.0f 0.0f; Entity.ElevationLocal == elevation; Entity.Size == v3 72.0f 72.0f 0.0f
                 Entity.UpImage == asset "Field" "InventoryButtonUp"
                 Entity.DownImage == asset "Field" "InventoryButtonDown"
                 Entity.EnabledLocal <== field --> fun field -> match field.Menu.MenuState with MenuItem _ -> false | _ -> true
                 Entity.ClickEvent ==> msg (menuItemsOpen ())]
             Content.button "TechButton"
                [Entity.PositionLocal == position - v3 0.0f 162.0f 0.0f; Entity.ElevationLocal == elevation; Entity.Size == v3 72.0f 72.0f 0.0f
                 Entity.UpImage == asset "Field" "TechButtonUp"
                 Entity.DownImage == asset "Field" "TechButtonDown"
                 Entity.EnabledLocal <== field --> fun field -> match field.Menu.MenuState with MenuTech _ -> false | _ -> true
                 Entity.ClickEvent ==> msg (menuTechOpen ())]
             Content.button "OptionsButton"
                [Entity.PositionLocal == position - v3 0.0f 243.0f 0.0f; Entity.ElevationLocal == elevation; Entity.Size == v3 72.0f 72.0f 0.0f
                 Entity.UpImage == asset "Field" "OptionsButtonUp"
                 Entity.DownImage == asset "Field" "OptionsButtonDown"
                 Entity.EnabledLocal <== field --> fun field -> match field.Menu.MenuState with MenuOptions -> false | _ -> true
                 Entity.ClickEvent ==> msg (menuOptionsOpen ())]
             Content.button "HelpButton"
                [Entity.PositionLocal == position - v3 0.0f 324.0f 0.0f; Entity.ElevationLocal == elevation; Entity.Size == v3 72.0f 72.0f 0.0f
                 Entity.UpImage == asset "Field" "HelpButtonUp"
                 Entity.DownImage == asset "Field" "HelpButtonDown"]
             Content.button "CloseButton"
                [Entity.PositionLocal == position - v3 0.0f 405.0f 0.0f; Entity.ElevationLocal == elevation; Entity.Size == v3 72.0f 72.0f 0.0f
                 Entity.UpImage == asset "Field" "CloseButtonUp"
                 Entity.DownImage == asset "Field" "CloseButtonDown"
                 Entity.ClickEvent ==> msg (menuClose ())]]

    let team (position : Vector3) elevation rows (field : Lens<Field, World>) filter fieldMsg =
        Content.entities field
            (fun field -> Map.map (fun _ teammate -> (teammate, field.Menu)) field.Team)
            (fun index teammateAndMenu ->
                let x = position.X + if index < rows then 0.0f else 252.0f + 48.0f
                let y = position.Y - single (index % rows) * 81.0f
                Content.button Gen.name
                    [Entity.PositionLocal == v3 x y 0.0f; Entity.ElevationLocal == elevation; Entity.Size == v3 252.0f 72.0f 0.0f
                     Entity.EnabledLocal <== teammateAndMenu --> fun (teammate, menu) -> filter teammate menu
                     Entity.Text <== teammateAndMenu --> fun (teammate, _) -> CharacterType.getName teammate.CharacterType
                     Entity.UpImage == Assets.Gui.ButtonBigUpImage
                     Entity.DownImage == Assets.Gui.ButtonBigDownImage
                     Entity.ClickEvent ==> msg (fieldMsg index)])

    let items (position : Vector3) elevation rows columns field fieldMsg =
        Content.entities field
            (fun (field : Field) -> pageItems rows field |> __c)
            (fun i selectionLens ->
                let x = if i < columns then position.X else position.X + 375.0f
                let y = position.Y - single (i % columns) * 81.0f
                Content.button Gen.name
                    [Entity.PositionLocal == v3 x y 0.0f; Entity.ElevationLocal == elevation; Entity.Size == v3 336.0f 72.0f 0.0f
                     Entity.Justification == Justified (JustifyLeft, JustifyMiddle); Entity.Margins == v3 16.0f 0.0f 0.0f
                     Entity.Text <== selectionLens --> fun (_, (itemType, countOpt)) ->
                        let itemName = ItemType.getName itemType
                        match countOpt with
                        | Some count when count > 1 -> itemName + String (Array.create (17 - itemName.Length) ' ') + "x" + string count
                        | _ -> itemName
                     Entity.EnabledLocal <== selectionLens --> fun (_, (itemType, _)) ->
                        match itemType with
                        | Consumable _ | Equipment _ -> true
                        | KeyItem _ | Stash _ -> false
                     Entity.UpImage == Assets.Gui.ButtonLongUpImage
                     Entity.DownImage == Assets.Gui.ButtonLongDownImage
                     Entity.ClickEvent ==> msg (fieldMsg selectionLens)])

    let techs (position : Vector3) elevation field fieldMsg =
        Content.entities field
            (fun (field : Field) ->
                match field.Menu.MenuState with
                | MenuTech menuTech ->
                    match Map.tryFind menuTech.TeammateIndex field.Team with
                    | Some teammate -> teammate.Techs |> Seq.index |> Map.ofSeq
                    | None -> Map.empty
                | _ -> Map.empty)
            (fun i techLens ->
                let x = position.X
                let y = position.Y - single i * 60.0f
                Content.button Gen.name
                    [Entity.PositionLocal == v3 x y 0.0f; Entity.ElevationLocal == elevation; Entity.Size == v3 336.0f 60.0f 0.0f
                     Entity.Justification == Justified (JustifyLeft, JustifyMiddle); Entity.Margins == v3 16.0f 0.0f 0.0f
                     Entity.Text <== techLens --> scstringm
                     Entity.EnabledLocal == false
                     Entity.UpImage == Assets.Gui.ButtonSquishedUpImage
                     Entity.DownImage == Assets.Gui.ButtonSquishedDownImage
                     Entity.ClickEvent ==> msg (fieldMsg i)])

    let dialog name elevation promptLeft promptRight (detokenizeAndDialogOpt : Lens<((string -> string) * Dialog) option, World>) =
        Content.entityOpt detokenizeAndDialogOpt $ fun detokenizeAndDialog ->
            Content.composite<TextDispatcher> name
                [Entity.Perimeter <== detokenizeAndDialog --> fun (_, dialog) ->
                    match dialog.DialogForm with
                    | DialogThin -> box3 (v3 -432.0f 150.0f 0.0f) (v3 864.0f 90.0f 0.0f)
                    | DialogThick -> box3 (v3 -432.0f 78.0f 0.0f) (v3 864.0f 174.0f 0.0f)
                    | DialogNarration -> box3 (v3 -432.0f 78.0f 0.0f) (v3 864.0f 174.0f 0.0f)
                 Entity.Elevation == elevation
                 Entity.BackgroundImageOpt <== detokenizeAndDialog --> fun (_, dialog) ->
                    match dialog.DialogForm with
                    | DialogThin -> Some Assets.Gui.DialogThinImage
                    | DialogThick -> Some Assets.Gui.DialogThickImage
                    | DialogNarration -> Some Assets.Default.ImageEmpty
                 Entity.Text <== detokenizeAndDialog --> fun (detokenize, dialog) ->
                    Dialog.getText detokenize dialog
                 Entity.Justification <== detokenizeAndDialog --> fun (_, dialog) ->
                    match dialog.DialogForm with
                    | DialogThin | DialogThick -> Unjustified true
                    | DialogNarration -> Justified (JustifyCenter, JustifyMiddle)
                 Entity.Margins == v3 30.0f 30.0f 0.0f]
                [Content.button "Left"
                    [Entity.PositionLocal == v3 186.0f 18.0f 0.0f; Entity.ElevationLocal == 2.0f; Entity.Size == v3 192.0f 48.0f 0.0f
                     Entity.VisibleLocal <== detokenizeAndDialog --> fun (detokenize, dialog) -> Option.isSome dialog.DialogPromptOpt && Dialog.isExhausted detokenize dialog
                     Entity.Text <== detokenizeAndDialog --> fun (_, dialog) -> match dialog.DialogPromptOpt with Some ((promptText, _), _) -> promptText | None -> ""
                     Entity.ClickEvent ==> msg promptLeft]
                 Content.button "Right"
                    [Entity.PositionLocal == v3 486.0f 18.0f 0.0f; Entity.ElevationLocal == 2.0f; Entity.Size == v3 192.0f 48.0f 0.0f
                     Entity.VisibleLocal <== detokenizeAndDialog --> fun (detokenize, dialog) -> Option.isSome dialog.DialogPromptOpt && Dialog.isExhausted detokenize dialog
                     Entity.Text <== detokenizeAndDialog --> fun (_, dialog) -> match dialog.DialogPromptOpt with Some (_, (promptText, _)) -> promptText | None -> ""
                     Entity.ClickEvent ==> msg promptRight]]