/*
 * (c) 2012 MOSA - The Managed Operating System Alliance
 *
 * Licensed under the terms of the New BSD License.
 *
 * Authors:
 *  Phil Garcia (tgiphil) <phil@thinkedge.com>
 */

using Mosa.TinyCPUSimulator;
using System.Collections.Generic;

namespace Mosa.Tool.Simulator
{
	public partial class StackFrameView : SimulatorDockContent
	{
		public StackFrameView()
		{
			InitializeComponent();
		}

		public override void UpdateDock(SimState simState)
		{
			listBox1.Items.Clear();

			bool force32 = (uint)simState.Values["Register.Size"] == 32;
			List<ulong[]> stack = simState.Values["StackFrame"] as List<ulong[]>;

			foreach (var entry in stack)
			{
				listBox1.Items.Add(listBox1.Items.Count.ToString("D2") + ": " + MainForm.Format(entry[0], force32) + " [" + MainForm.Format(entry[1], force32) + "]");
			}

			this.Refresh();
		}
	}
}