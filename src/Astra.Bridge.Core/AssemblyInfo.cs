// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;

// The foundation (both runtimes build it as Astra.Unity) drives what an integration only uses:
// GameLink's Attach, Pump and Detach. The tests reach the same.
[assembly: InternalsVisibleTo("Astra.Unity")]
[assembly: InternalsVisibleTo("Astra.Bridge.Core.Tests")]
