// Explicit global usings for vigil_ui.
//
// The WPF SDK compiles XAML through an intermediate "wpftmp" project that does not always inherit
// ImplicitUsings (random CS0246); these it always sees.

global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
global using Vigil.Ipc;
