﻿namespace System
open System.Reflection

[<assembly: AssemblyVersionAttribute("0.6.9")>]
[<assembly: AssemblyFileVersionAttribute("0.6.9")>]
do ()

module internal AssemblyVersionInformation =
    let [<Literal>] Version = "0.6.9"
