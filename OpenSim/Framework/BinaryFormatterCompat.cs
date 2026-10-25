/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace OpenSim.Framework
{
    /// <summary>
    /// .NET 9+ ships System.Runtime.Serialization.Formatters as a stub (assembly version 8.1.0.0) whose
    /// BinaryFormatter always throws. The System.Runtime.Serialization.Formatters NuGet package (10.0.0.0) restores
    /// it and is deployed to bin/, but OpenSim and Robust run without a deps.json, so the runtime only knows the
    /// framework stub and fails to bind 10.0.0.0. This resolver loads the package copy from the application folder.
    /// Used by the Flotsam asset disk cache, YEngine script state and keyframe motion.
    /// </summary>
    public static class BinaryFormatterCompat
    {
        private const string AssemblyName = "System.Runtime.Serialization.Formatters";

        // The default context maps this name to the framework stub, so loading the package copy there by path just
        // returns the stub again. It gets a context of its own; its own dependencies fall back to the default one.
        private static readonly Lazy<Assembly> s_package = new Lazy<Assembly>(() =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, AssemblyName + ".dll");
            return File.Exists(path)
                ? new AssemblyLoadContext("DreamGrid.BinaryFormatterCompat").LoadFromAssemblyPath(path)
                : null;
        });

        /// <summary>Call first thing in Main, before any plugin loads.</summary>
        public static void Register()
        {
            AssemblyLoadContext.Default.Resolving += Resolve;
        }

        private static Assembly Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            return string.Equals(name.Name, AssemblyName, StringComparison.Ordinal) ? s_package.Value : null;
        }
    }
}
