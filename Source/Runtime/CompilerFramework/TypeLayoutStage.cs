/*
 * (c) 2008 MOSA - The Managed Operating System Alliance
 *
 * Licensed under the terms of the New BSD License.
 *
 * Authors:
 *  Michael Ruck (grover) <sharpos@michaelruck.de>
 */

using System;
using System.Diagnostics;
using System.IO;

using Mosa.Runtime.Linker;
using Mosa.Runtime.Metadata;
using Mosa.Runtime.Metadata.Tables;
using Mosa.Runtime.Vm;
using Mosa.Runtime.Metadata.Signatures;
using System.Collections.Generic;

namespace Mosa.Runtime.CompilerFramework
{
	/// <summary>
	/// Performs memory layout of a type for compilation.
	/// </summary>
	public sealed class TypeLayoutStage : BaseAssemblyCompilerStage, IAssemblyCompilerStage
	{
		#region Data members

		private IAssemblyLinker linker;

		private int nativePointerAlignment;

		private int nativePointerSize;

		private Dictionary<RuntimeType, IList<RuntimeMethod>> methodTables = new Dictionary<RuntimeType, IList<RuntimeMethod>>();

		#endregion // Data members

		#region IPipelineStage members

		/// <summary>
		/// Retrieves the name of the compilation stage.
		/// </summary>
		/// <value>The name of the compilation stage.</value>
		string IPipelineStage.Name { get { return @"Type Layout"; } }

		#endregion // IPipelineStage

		#region IAssemblyCompilerStage members

		void IAssemblyCompilerStage.Setup(AssemblyCompiler compiler)
		{
			base.Setup(compiler);

			linker = RetrieveAssemblyLinkerFromCompiler();

			architecture.GetTypeRequirements(BuiltInSigType.IntPtr, out nativePointerSize, out nativePointerAlignment);
		}

		void IAssemblyCompilerStage.Run()
		{
			// Enumerate all types and do an appropriate type layout
			foreach (RuntimeType type in typeSystem.GetCompiledTypes())
			{
				if (type.IsModule)
					continue;

				if (type.IsGeneric || type.IsDelegate)
					continue;

				if (type.IsInterface)
				{
					CreateInterfaceMethodTable(type);
				}
				else
				{
					if (!IsExplicitLayoutRequestedByType(type))
					{
						CreateSequentialLayout(type);
					}
					else
					{
						CreateExplicitLayout(type);
					}

					BuildMethodTable(type);

					foreach (RuntimeType interfaceType in type.Interfaces)
					{
						this.BuildInterfaceTable(type, interfaceType);
					}
				}

				AllocateStaticFields(type);

				int i = 0;
				Debug.WriteLine("Type: " + type.ToString());
				foreach (RuntimeMethod method in GetMethodTable(type))
				{
					Debug.WriteLine("    " + i.ToString() + ":" + method.ToString());
					i++;
				}
			}
		}

		#endregion // IAssemblyCompilerStage members

		private bool IsExplicitLayoutRequestedByType(RuntimeType type)
		{
			return (type.Attributes & TypeAttributes.LayoutMask) == TypeAttributes.ExplicitLayout;
		}

		private void BuildInterfaceTable(RuntimeType type, RuntimeType interfaceType)
		{
			List<RuntimeMethod> interfaceMethods = new List<RuntimeMethod>(interfaceType.Methods);
			List<RuntimeMethod> methodTable = new List<RuntimeMethod>(interfaceMethods.Count);
			while (methodTable.Count < methodTable.Capacity)
			{
				methodTable.Add(null);
			}

			ScanExplicitInterfaceImplementations(type, methodTable, interfaceMethods);
			AddImplicitInterfaceImplementations(type, methodTable, interfaceMethods);

			AskLinkerToCreateMethodTable(type.FullName + "$" + interfaceType.FullName, methodTable);
		}

		private void ScanExplicitInterfaceImplementations(RuntimeType type, IList<RuntimeMethod> methodTable, IList<RuntimeMethod> interfaceMethods)
		{
			IMetadataProvider metadata = type.MetadataModule.Metadata;
			TokenTypes maxToken = metadata.GetMaxTokenValue(TokenTypes.MethodImpl);
			for (TokenTypes token = TokenTypes.MethodImpl + 1; token <= maxToken; token++)
			{
				MethodImplRow row = metadata.ReadMethodImplRow(token);
				if (row.ClassTableIdx == (TokenTypes)type.Token)
				{
					int interfaceSlot = -1;

					foreach (RuntimeMethod interfaceMethod in interfaceMethods)
					{
						interfaceSlot++;

						if (interfaceMethod != null && (TokenTypes)interfaceMethod.Token == row.MethodDeclarationTableIdx)
						{
							methodTable[interfaceSlot] = this.FindMethodByToken(type, row.MethodBodyTableIdx);
							break;
						}
					}
				}
			}
		}

		private RuntimeMethod FindMethodByToken(RuntimeType type, TokenTypes methodToken)
		{
			foreach (RuntimeMethod method in type.Methods)
			{
				if ((TokenTypes)method.Token == methodToken)
				{
					return method;
				}
			}

			throw new InvalidOperationException(@"Failed to find explicit interface method implementation.");
		}

		private void AddImplicitInterfaceImplementations(RuntimeType type, IList<RuntimeMethod> methodTable, IList<RuntimeMethod> interfaceMethods)
		{
			for (int slot = 0; slot < methodTable.Count; slot++)
			{
				if (methodTable[slot] == null)
				{
					methodTable[slot] = this.FindInterfaceMethod(type, interfaceMethods[slot]);
				}
			}
		}

		private RuntimeMethod FindInterfaceMethod(RuntimeType type, RuntimeMethod interfaceMethod)
		{
			foreach (RuntimeMethod method in type.Methods)
			{
				if (interfaceMethod.Name.Equals(method.Name) && interfaceMethod.Signature.Matches(method.Signature))
				{
					return method;
				}
			}

			throw new InvalidOperationException(@"Failed to find implicit interface implementation.");
		}

		private void BuildMethodTable(RuntimeType type)
		{
			IList<RuntimeMethod> methodTable = CreateMethodTable(type);
			AskLinkerToCreateMethodTable(type.FullName, methodTable);
		}

		/// <summary>
		/// Creates the interface method table.
		/// </summary>
		/// <param name="type">The type.</param>
		private void CreateInterfaceMethodTable(RuntimeType type)
		{
			CreateMethodTable(type);
		}

		/// <summary>
		/// Creates the method table.
		/// </summary>
		/// <param name="type">The type.</param>
		/// <returns></returns>
		private IList<RuntimeMethod> CreateMethodTable(RuntimeType type)
		{
			IList<RuntimeMethod> methodTable = GetMethodTable(type);

			if (methodTable != null)
				return methodTable;

			methodTable = CreateMethodTableFromBaseType(type);

			foreach (RuntimeMethod method in type.Methods)
			{
				MethodAttributes attributes = method.Attributes;
				if ((attributes & MethodAttributes.Virtual) == MethodAttributes.Virtual)
				{
					int slot = methodTable.Count;

					if ((attributes & MethodAttributes.NewSlot) != MethodAttributes.NewSlot)
					{
						slot = FindOverrideSlot(methodTable, method);
					}

					method.MethodTableSlot = slot;
					if (slot == methodTable.Count)
					{
						methodTable.Add(method);
					}
					else
					{
						methodTable[slot] = method;
					}
				}
			}

			return methodTable;
		}

		private int FindOverrideSlot(IList<RuntimeMethod> methodTable, RuntimeMethod method)
		{
			foreach (RuntimeMethod baseMethod in methodTable)
			{
				if (baseMethod.Name.Equals(method.Name) && baseMethod.Signature.Matches(method.Signature))
				{
					return baseMethod.MethodTableSlot;
				}
			}

			throw new InvalidOperationException(@"Failed to find override method slot.");
		}

		private IList<RuntimeMethod> GetMethodTable(RuntimeType type)
		{
			IList<RuntimeMethod> methods;

			if (methodTables.TryGetValue(type, out methods))
				return methods;
			else
				return null;
		}

		/// <summary>
		/// Creates the method table from the base type.
		/// </summary>
		/// <param name="type">The type.</param>
		/// <returns></returns>
		private List<RuntimeMethod> CreateMethodTableFromBaseType(RuntimeType type)
		{
			List<RuntimeMethod> methodTable = new List<RuntimeMethod>();

			if (type.BaseType != null)
			{
				IList<RuntimeMethod> baseMethodTable = GetMethodTable(type.BaseType);

				if (baseMethodTable == null)
				{
					// Method table for the base type has not been create yet, so create it now
					baseMethodTable = CreateMethodTable(type.BaseType);
				}

				methodTable = new List<RuntimeMethod>(baseMethodTable);
			}

			methodTables.Add(type, methodTable);
			return methodTable;
		}

		private void AskLinkerToCreateMethodTable(string name, IList<RuntimeMethod> methodTable)
		{
			// HINT: The method table is offset by a single pointer, which contains the type information 
			// pointer. Used to realize object.GetType()

			string methodTableSymbolName = name + @"$mtable";
			int methodTableSize = nativePointerSize + methodTable.Count * nativePointerSize;

			using (Stream stream = linker.Allocate(methodTableSymbolName, SectionKind.Text, methodTableSize, nativePointerAlignment))
			{
				stream.Position = methodTableSize;
			}

			int offset = nativePointerSize;

			foreach (RuntimeMethod method in methodTable)
			{
				if (!IsAbstract(method))
				{
					string methodSymbol = method.ToString();
					linker.Link(LinkType.AbsoluteAddress | LinkType.I4, methodTableSymbolName, offset, 0, methodSymbol, IntPtr.Zero);
				}

				offset += nativePointerSize;
			}
		}

		private bool IsAbstract(RuntimeMethod method)
		{
			return (method.Attributes & MethodAttributes.Abstract) == MethodAttributes.Abstract;
		}

		/// <summary>
		/// Performs a sequential layout of the type.
		/// </summary>
		/// <param name="type">The type.</param>
		private void CreateSequentialLayout(RuntimeType type)
		{
			Debug.Assert(type != null, @"No type given.");

			// Receives the size/alignment
			int packingSize = type.Pack;
			// Instance size
			int typeSize = 0;

			int fieldSize;
			int typeAlignment;

			RuntimeType baseType = type.BaseType;
			if (baseType != null)
			{
				typeSize = baseType.Size;
			}

			foreach (RuntimeField field in type.Fields)
			{
				architecture.GetTypeRequirements(field.SignatureType, out fieldSize, out typeAlignment);

				// Pad the field in the type
				if (packingSize != 0)
				{
					int padding = (typeSize % packingSize);
					typeSize += padding;
				}

				// Set the field address
				field.Address = new IntPtr(typeSize);
				typeSize += fieldSize;
			}

			type.Size = typeSize;
		}

		private bool IsLiteralField(RuntimeField field)
		{
			return (field.Attributes & FieldAttributes.Literal) == FieldAttributes.Literal;
		}

		private bool IsStaticField(RuntimeField field)
		{
			return (field.Attributes & FieldAttributes.Static) == FieldAttributes.Static;
		}

		/// <summary>
		/// Applies the explicit layout to the given type.
		/// </summary>
		/// <param name="type">The type.</param>
		private void CreateExplicitLayout(RuntimeType type)
		{
			Debug.Assert(type != null, @"No type given.");
			Debug.Assert(type.BaseType.Size != 0, @"Type size not set for explicit layout.");

			foreach (RuntimeField field in type.Fields)
			{
				// Explicit layout assigns a physical offset From the start of the structure
				// to the field. We just assign this offset.
				Debug.Assert(field.Address.ToInt64() != 0, @"Non-static field doesn't have layout!");
			}
		}

		private void AllocateStaticFields(RuntimeType type)
		{
			foreach (RuntimeField field in type.Fields)
			{
				if (IsStaticField(field) && !IsLiteralField(field))
				{
					// Assign a memory slot to the static & initialize it, if there's initial data set
					CreateStaticField(field);
				}
			}
		}

		/// <summary>
		/// Allocates memory for the static field and initializes it.
		/// </summary>
		/// <param name="field">The field.</param>
		private void CreateStaticField(RuntimeField field)
		{
			Debug.Assert(field != null, @"No field given.");

			// Determine the size of the type & alignment requirements
			int size, alignment;
			this.architecture.GetTypeRequirements(field.SignatureType, out size, out alignment);

			if (field.SignatureType.Type == CilElementType.ValueType)
				size = ObjectModelUtility.ComputeTypeSize(field.DeclaringType, (field.SignatureType as Metadata.Signatures.ValueTypeSigType).Token, field.ModuleTypeSystem, architecture);

			// The linker section to move this field into
			SectionKind section;
			// Does this field have an RVA?
			if (field.RVA != 0)
			{
				// FIXME: Move a static field into ROData, if it is read-only and can be initialized
				// using static analysis
				section = SectionKind.Data;
			}
			else
			{
				section = SectionKind.BSS;
			}

			this.AllocateSpace(field, section, size, alignment);
		}

		private void AllocateSpace(RuntimeField field, SectionKind section, int size, int alignment)
		{
			using (Stream stream = this.linker.Allocate(field.ToString(), section, size, alignment))
			{
				if (field.RVA != 0)
				{
					this.InitializeStaticValueFromRVA(stream, size, field);
				}
				else
				{
					WriteDummyBytes(stream, size);
				}
			}
		}

		private void InitializeStaticValueFromRVA(Stream stream, int size, RuntimeField field)
		{
			using (Stream source = field.MetadataModule.GetDataSection((long)field.RVA))
			{
				byte[] data = new byte[size];
				source.Read(data, 0, size);
				stream.Write(data, 0, size);
			}
		}

		private static void WriteDummyBytes(Stream stream, int size)
		{
			stream.Write(new byte[size], 0, size);
		}

	}
}
