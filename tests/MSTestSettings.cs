global using System;
global using System.Collections.Generic;
global using System.Linq;
global using System.Threading.Tasks;

using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize( Scope = ExecutionScope.MethodLevel )]
