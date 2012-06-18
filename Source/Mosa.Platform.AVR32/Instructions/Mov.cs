/*
 * (c) 2012 MOSA - The Managed Operating System Alliance
 *
 * Licensed under the terms of the New BSD License.
 *
 * Authors:
 *  Phil Garcia (tgiphil) <phil@thinkedge.com>
 *  Pascal Delprat (pdelprat) <pascal.delprat@online.fr>    
 */

using System;
using Mosa.Compiler.Framework;
using Mosa.Compiler.Framework.Operands;

namespace Mosa.Platform.AVR32.Instructions
{
	/// <summary>
	/// Mov Instruction
	/// Supported Format:
	///     mov Rd, imm (8 bits)
	///     mov Rd, imm (21 bits)
	///     mov Rd, Rs
	/// </summary>
	public class Mov : AVR32Instruction
	{

		#region Methods

		/// <summary>
		/// Emits the specified platform instruction.
		/// </summary>
		/// <param name="context">The context.</param>
		/// <param name="emitter">The emitter.</param>
		protected override void Emit(Context context, MachineCodeEmitter emitter)
		{
            if (context.Result is DefinedRegisterOperand && context.Operand1 is ConstantOperand)
            {
                DefinedRegisterOperand reg = context.Result as DefinedRegisterOperand;
                ConstantOperand op = context.Operand1 as ConstantOperand;

                int value = 0;

                if (IsConstantBetween(op, -128, 127, out value))
                {
                    emitter.EmitK8immediateAndSingleRegister(0x01, (sbyte)value, (byte)reg.Register.RegisterCode); // mov Rd, Imm (k8)
                }
                else
                    if (IsConstantBetween(op, -1048576, 1048575, out value))
                    {
                        emitter.EmitRegisterOrConditionCodeAndK21(0x03, (byte)reg.Register.RegisterCode, value); // mov Rd, Imm (k21)
                    }
                    else
                        throw new OverflowException();
            }
            else
                if ((context.Result is DefinedRegisterOperand) && (context.Operand1 is DefinedRegisterOperand))
                {
                    DefinedRegisterOperand destination = context.Result as DefinedRegisterOperand;
                    DefinedRegisterOperand source = context.Operand1 as DefinedRegisterOperand;

                    emitter.EmitTwoRegisterInstructions(0x09, (byte)source.Register.RegisterCode, (byte)destination.Register.RegisterCode); // mov Rd, Rs
                }
                else
                    throw new Exception("Not supported combination of operands");
		}

		/// <summary>
		/// Allows visitor based dispatch for this instruction object.
		/// </summary>
		/// <param name="visitor">The visitor object.</param>
		/// <param name="context">The context.</param>
		public override void Visit(IAVR32Visitor visitor, Context context)
		{
			visitor.Mov(context);
		}

		#endregion // Methods

	}
}
