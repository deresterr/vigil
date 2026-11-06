// Global using directives dla vigil-lockscreen.
//
// WPF SDK generuje "wpftmp" intermediate csproj do kompilacji XAML, który nie zawsze
// dziedziczy ImplicitUsings z głównego csproj — efekt: random CS0246 dla typów które
// powinny być dostępne. Tutaj są jawne globals które wpftmp na pewno zobaczy.

global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
