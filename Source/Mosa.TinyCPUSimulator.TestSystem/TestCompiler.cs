﻿/*
 * (c) 2014 MOSA - The Managed Operating System Alliance
 *
 * Licensed under the terms of the New BSD License.
 *
 * Authors:
 *  Phil Garcia (tgiphil) <phil@thinkedge.com>
 */

using Mosa.Compiler.Framework;
using Mosa.Compiler.Linker;
using Mosa.Compiler.MosaTypeSystem;
using Mosa.Compiler.Trace;
using Mosa.TinyCPUSimulator.Adaptor;
using System;
using System.Diagnostics;

namespace Mosa.TinyCPUSimulator.TestSystem
{
	public class TestCompiler : ITraceListener
	{
		protected MosaCompiler compiler = new MosaCompiler();

		protected BaseTestPlatform platform;

		protected ISimAdapter adapter;

		protected ISimAdapter simAdapter;

		protected SimLinker linker;

		public TestCompiler(BaseTestPlatform platform)
		{
			this.platform = platform;

			simAdapter = platform.CreateSimAdaptor();

			compiler.CompilerTrace.TraceFilter.Active = false;
			compiler.CompilerTrace.TraceListener = this;

			compiler.CompilerOptions.EnableOptimizations = true;
			compiler.CompilerOptions.EnableSSA = true;
			compiler.CompilerOptions.EnablePromoteTemporaryVariablesOptimization = true;
			compiler.CompilerOptions.EnableSparseConditionalConstantPropagation = true;

			compiler.CompilerOptions.Architecture = platform.CreateArchitecture();
			compiler.CompilerOptions.LinkerFactory = delegate { return new SimLinker(simAdapter); };
			compiler.CompilerFactory = delegate { return new SimCompiler(simAdapter); };

			CompileTestCode();
		}

		protected void CompileTestCode()
		{
			platform.InitializeSimulation(simAdapter);

			var moduleLoader = new MosaModuleLoader();

			moduleLoader.AddPrivatePath(System.IO.Directory.GetCurrentDirectory());
			moduleLoader.LoadModuleFromFile("mscorlib.dll");
			moduleLoader.LoadModuleFromFile("Mosa.Platform.Internal." + platform.Name + ".dll");
			moduleLoader.LoadModuleFromFile("Mosa.Test.Collection.dll");
			moduleLoader.LoadModuleFromFile("Mosa.Kernel." + platform.Name + "Test.dll");

			compiler.Load(TypeSystem.Load(moduleLoader.CreateMetadata()));

			compiler.Execute();

			linker = compiler.Linker as SimLinker;

			//simAdapter.SimCPU.Monitor.DebugOutput = true; // DEBUG OPTION

			Run<int>(string.Empty, "Default", "AssemblyInit", true);

			//simAdapter.SimCPU.Monitor.DebugOutput = true; // DEBUG OPTION
		}

		public T Run<T>(string ns, string type, string method, params object[] parameters)
		{
			return Run<T>(ns, type, method, true, parameters);
		}

		protected T Run<T>(string ns, string type, string method, bool reset, params object[] parameters)
		{
			if (reset)
			{
				// reset the stack
				platform.ResetSimulation(simAdapter);

				//Run<int>("Mosa.Kernel.x86Test", "KernelMemory", "SetMemory", false, new object[] { (uint)0x00900000 });
			}

			// Find the test method to execute
			MosaMethod runtimeMethod = FindMethod(
				ns,
				type,
				method,
				parameters
			);

			Debug.Assert(runtimeMethod != null, runtimeMethod.ToString());

			var symbol = linker.GetSymbol(runtimeMethod.FullName, SectionKind.Text);

			ulong address = (ulong)symbol.VirtualAddress;

			platform.PopulateStack(simAdapter, parameters);

			platform.PrepareToExecuteMethod(simAdapter, address);

			simAdapter.SimCPU.Monitor.BreakAtTick = simAdapter.SimCPU.Tick + 500000; // nothing should take this long
			simAdapter.SimCPU.Execute();

			if (simAdapter.SimCPU.Monitor.BreakAtTick == simAdapter.SimCPU.Tick)
				throw new Exception("Aborted. Method did not complete under 500000 ticks. " + simAdapter.SimCPU.Tick.ToString());

			if (runtimeMethod.Signature.ReturnType.IsVoid)
				return default(T);

			object result = platform.GetResult(simAdapter, runtimeMethod.Signature.ReturnType);

			try
			{
				if (default(T) is ValueType)
					return (T)result;
				else
					return default(T);
			}
			catch (InvalidCastException e)
			{
				Debug.Assert(false, String.Format("Failed to convert result {0} of destinationpe {1} destination type {2}.", result, result.GetType(), typeof(T).ToString()));
				throw e;
			}
		}

		private MosaMethod FindMethod(string ns, string type, string method, params object[] parameters)
		{
			foreach (var t in compiler.TypeSystem.AllTypes)
			{
				if (t.Name != type)
					continue;

				if (!string.IsNullOrEmpty(ns))
					if (t.Namespace != ns)
						continue;

				foreach (var m in t.Methods)
				{
					if (m.Name == method)
					{
						if (m.Signature.Parameters.Count == parameters.Length)
						{
							return m;
						}
					}
				}

				break;
			}

			return null;
		}

		void ITraceListener.OnNewCompilerTraceEvent(CompilerEvent compilerStage, string info, int threadID)
		{
		}

		void ITraceListener.OnUpdatedCompilerProgress(int totalMethods, int completedMethods)
		{
		}

		void ITraceListener.OnNewTraceLog(TraceLog traceLog)
		{
		}
	}
}