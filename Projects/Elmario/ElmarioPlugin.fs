﻿namespace Elmario
open System
open Nu

// this is a plugin for the Nu game engine that directs the execution of your application and editor
type ElmarioPlugin () =
    inherit NuPlugin ()