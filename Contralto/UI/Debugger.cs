﻿/*  
    This file is part of ContrAlto.

    ContrAlto is free software: you can redistribute it and/or modify
    it under the terms of the GNU Affero General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    ContrAlto is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU Affero General Public License for more details.

    You should have received a copy of the GNU Affero General Public License
    along with ContrAlto.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

using Contralto.CPU;
using System.Text;

namespace Contralto
{
    /// <summary>
    /// A basic & hacky debugger.  To be improved.
    /// </summary>
    public partial class Debugger : Form
    {
        public Debugger(AltoSystem system, ExecutionController controller)
        {
            _system = system;
            _controller = controller;
            _microcodeBreakpointEnabled = new bool[5, 1024];
            _novaBreakpointEnabled = new bool[65536];

            _controller.StepCallback += OnExecutionStep;
            _controller.ErrorCallback += OnExecutionError;

            // Pick up the current execution status (if the main window hands us a running
            // system, we want to know).
            _execType = _controller.IsRunning ? ExecutionType.Normal : ExecutionType.None;

            InitializeComponent();
            InitControls();
            RefreshUI();
        }

        public void LoadSourceCode(MicrocodeBank bank, string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(path, "Microcode path must be specified.");
            }

            DataGridView view = bank == MicrocodeBank.ROM0 ? _rom0SourceViewer : _rom1SourceViewer;

            using (StreamReader sr = new StreamReader(path))
            {
                while (!sr.EndOfStream)
                {
                    string line = sr.ReadLine();

                    SourceLine src = new SourceLine(line);

                    int i = view.Rows.Add(
                        false,  // breakpoint
                        GetTextForTask(src.Task),
                        src.Address,
                        src.Text);

                    // Give the row a color based on the task
                    view.Rows[i].DefaultCellStyle.BackColor = GetColorForTask(src.Task);

                    // Tag the row based on the PROM address (if any) to make it easy to find.
                    if (!String.IsNullOrEmpty(src.Address))
                    {
                        view.Rows[i].Tag = Convert.ToUInt16(src.Address, 8);
                    }
                }
            }

            // Ensure the UI view gets refreshed to display the current MPC source
            Refresh();
        }

        public override void Refresh()
        {
            base.Refresh();

            RefreshUI();
        }


        private void OnDebuggerClosed(object sender, FormClosedEventArgs e)
        {
            _controller.StepCallback -= OnExecutionStep;
            _controller.ErrorCallback -= OnExecutionError;
        }

        private void RefreshUI()
        {
            // Registers
            for (int i = 0; i < 32; i++)
            {
                _registerData.Rows[i].Cells[0].Value = Conversion.ToOctal(i, 2);
                _registerData.Rows[i].Cells[1].Value = Conversion.ToOctal(_system.CPU.R[i], 6);
                _registerData.Rows[i].Cells[2].Value = Conversion.ToOctal(_system.CPU.S[0][i], 6);
            }

            // Tasks
            for (int i = 0; i < 16; i++)
            {
                _taskData.Rows[i].Cells[0].Value = GetTextForTask((TaskType)i);
                _taskData.Rows[i].Cells[1].Value = GetTextForTaskState(_system.CPU.Tasks[i]);
                _taskData.Rows[i].Cells[2].Value = GetTextForTaskBank(_system.CPU.Tasks[i]);
                _taskData.Rows[i].Cells[3].Value =
                    _system.CPU.Tasks[i] != null ? Conversion.ToOctal(_system.CPU.Tasks[i].MPC, 4) : String.Empty;
            }

            // Other registers            
            _otherRegs.Rows[0].Cells[1].Value = Conversion.ToOctal(_system.CPU.L, 6);
            _otherRegs.Rows[1].Cells[1].Value = Conversion.ToOctal(_system.CPU.T, 6);
            _otherRegs.Rows[2].Cells[1].Value = Conversion.ToOctal(_system.CPU.M, 6);
            _otherRegs.Rows[3].Cells[1].Value = Conversion.ToOctal(_system.CPU.IR, 6);
            _otherRegs.Rows[4].Cells[1].Value = Conversion.ToOctal(_system.CPU.ALUC0, 1);
            _otherRegs.Rows[5].Cells[1].Value = Conversion.ToOctal(_system.MemoryBus.MAR, 6);
            _otherRegs.Rows[6].Cells[1].Value = Conversion.ToOctal(_system.MemoryBus.MDLow, 6);
            _otherRegs.Rows[7].Cells[1].Value = Conversion.ToOctal(_system.MemoryBus.MDHigh, 6);
            _otherRegs.Rows[8].Cells[1].Value = Conversion.ToOctal(_system.MemoryBus.MDWrite, 6);
            _otherRegs.Rows[9].Cells[1].Value = Conversion.ToOctal(_system.MemoryBus.Cycle & 0x3f, 2);

            // Reserved memory locations
            for (int i = 0; i < _reservedMemoryEntries.Length; i++)
            {
                _reservedMemory.Rows[i].Cells[2].Value =
                    Conversion.ToOctal(_system.MemoryBus.DebugReadWord(_reservedMemoryEntries[i].Address), 6);
            }

            //
            // Select active tab based on current UCode bank
            MicrocodeBank bank = UCodeMemory.GetBank(_system.CPU.CurrentTask.TaskType);

            switch (bank)
            {
                case MicrocodeBank.ROM0:
                    SourceTabs.SelectedIndex = 0;
                    break;

                case MicrocodeBank.ROM1:
                    SourceTabs.SelectedIndex = 1;
                    break;

                case MicrocodeBank.RAM0:
                    SourceTabs.SelectedIndex = 2;
                    break;
            }

            RefreshMicrocodeDisassembly(_system.CPU.CurrentTask.MPC);

            // Highlight the nova memory location corresponding to the emulator PC.
            // TODO: this should be configurable
            ushort pc = _system.CPU.R[6];

            HighlightNovaSourceLine(pc);

            // Exec state
            switch (_execState)
            {
                case ExecutionState.Stopped:
                    ExecutionStateLabel.Text = "Stopped";
                    break;

                case ExecutionState.SingleStep:
                    ExecutionStateLabel.Text = "Stepping";
                    break;

                case ExecutionState.AutoStep:
                    ExecutionStateLabel.Text = "Stepping (auto)";
                    break;

                case ExecutionState.Running:
                    ExecutionStateLabel.Text = "Running";
                    break;

                case ExecutionState.BreakpointStop:
                    ExecutionStateLabel.Text = "Stopped (bkpt)";
                    break;

                case ExecutionState.InternalError:
                    ExecutionStateLabel.Text = String.Format("Stopped (error {0})", _lastExceptionText);
                    break;
            }

            this.BringToFront();
        }

        private void RefreshMicrocodeDisassembly(ushort address)
        {
            // Update non-ROM code listings, depending on the currently active tab
            switch (SourceTabs.SelectedIndex)
            {
                case 0:
                    // Find the right source line and highlight it.
                    HighlightMicrocodeSourceLine(_rom0SourceViewer, address);
                    break;

                case 1:
                    HighlightMicrocodeSourceLine(_rom1SourceViewer, address);
                    break;

                case 2:
                    HighlightMicrocodeDisassemblyLine(_ram0SourceViewer, address);
                    break;

                case 3:
                    HighlightMicrocodeDisassemblyLine(_ram1SourceViewer, address);
                    break;

                case 4:
                    HighlightMicrocodeDisassemblyLine(_ram2SourceViewer, address);
                    break;
            }
        }

        private void InitControls()
        {
            for (int i = 0; i < 32; i++)
            {
                _registerData.Rows.Add(-1, -1, -1);
            }

            for (int i = 0; i < 16; i++)
            {
                _taskData.Rows.Add("0", "0", "0");
            }

            // TODO: handle extended memory
            _memoryData.RowCount = 65536;
            _ram0SourceViewer.RowCount = 1024;
            _ram1SourceViewer.RowCount = 1024;
            _ram2SourceViewer.RowCount = 1024;

            _otherRegs.Rows.Add("L", "0");
            _otherRegs.Rows.Add("T", "0");
            _otherRegs.Rows.Add("M", "0");
            _otherRegs.Rows.Add("IR", "0");
            _otherRegs.Rows.Add("ALUC0", "0");
            _otherRegs.Rows.Add("MAR", "0");
            _otherRegs.Rows.Add("←MDL", "0");
            _otherRegs.Rows.Add("←MDH", "0");
            _otherRegs.Rows.Add("MD←", "0");
            _otherRegs.Rows.Add("MCycle", "0");

            for (int i = 0; i < _reservedMemoryEntries.Length; i++)
            {
                _reservedMemory.Rows.Add(
                    Conversion.ToOctal(_reservedMemoryEntries[i].Address, 3),
                    _reservedMemoryEntries[i].Name,
                    Conversion.ToOctal(0, 6));
            }            

            ContextMenuStrip memoryContextMenu = new ContextMenuStrip();
            memoryContextMenu.Items.Add("Copy all");
            memoryContextMenu.ItemClicked += OnMemoryContextMenuItemClicked;

            _memoryData.ContextMenuStrip = memoryContextMenu;
            
        }

        private void OnMemoryContextMenuItemClicked(object sender, ToolStripItemClickedEventArgs e)
        {        
            StringBuilder sb = new StringBuilder();

            for(int i=0;i<65536;i++)
            {
                sb.AppendFormat("{0}:{1} {2}\r\n",
                    Conversion.ToOctal(i, 6),
                   _memoryData.Rows[i].Cells[2].Value,
                   _memoryData.Rows[i].Cells[3].Value);
            }

            Clipboard.SetText(sb.ToString());

        }


        /// <summary>
        /// Handle breakpoint placement on column 0.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Rom0SourceViewCellClick(object sender, DataGridViewCellEventArgs e)
        {
            // Check for breakpoint column click.
            if (e.ColumnIndex == 0)
            {
                SetBreakpointFromCellClickForSourceView(MicrocodeBank.ROM0, e.RowIndex);
            }
        }

        private void Rom1SourceViewCellClick(object sender, DataGridViewCellEventArgs e)
        {
            // Check for breakpoint column click.
            if (e.ColumnIndex == 0)
            {
                SetBreakpointFromCellClickForSourceView(MicrocodeBank.ROM1, e.RowIndex);
            }
        }

        private void Ram0SourceViewCellClick(object sender, DataGridViewCellEventArgs e)
        {
            // Check for breakpoint column click.
            if (e.ColumnIndex == 0)
            {
                SetBreakpointFromCellClickForDisassemblyView(MicrocodeBank.RAM0, e.RowIndex);
            }
        }

        private void Ram1SourceViewCellClick(object sender, DataGridViewCellEventArgs e)
        {
            // Check for breakpoint column click.
            if (e.ColumnIndex == 0)
            {
                SetBreakpointFromCellClickForDisassemblyView(MicrocodeBank.RAM1, e.RowIndex);
            }
        }

        private void Ram2SourceViewCellClick(object sender, DataGridViewCellEventArgs e)
        {
            // Check for breakpoint column click.
            if (e.ColumnIndex == 0)
            {
                SetBreakpointFromCellClickForDisassemblyView(MicrocodeBank.RAM2, e.RowIndex);
            }
        }

        private void SetBreakpointFromCellClickForSourceView(MicrocodeBank bank, int index)
        {
            DataGridView view = null;

            switch (bank)
            {
                case MicrocodeBank.ROM0:
                    view = _rom0SourceViewer;
                    break;

                case MicrocodeBank.ROM1:
                    view = _rom1SourceViewer;
                    break;

                default:
                    throw new InvalidOperationException("Bank does not have a source view.");
            }

            // See if this is a source line, if so check/uncheck the box
            // and set/unset a breakpoint for the line
            if (view.Rows[index].Tag != null)
            {
                bool value = (bool)view.Rows[index].Cells[0].Value;
                view.Rows[index].Cells[0].Value = !value;

                ModifyMicrocodeBreakpoint(bank, (UInt16)view.Rows[index].Tag, !value);
            }
        }

        private void SetBreakpointFromCellClickForDisassemblyView(MicrocodeBank bank, int index)
        {
            DataGridView view = null;

            switch (bank)
            {
                case MicrocodeBank.RAM0:
                    view = _rom0SourceViewer;
                    break;

                case MicrocodeBank.RAM1:
                    view = _ram1SourceViewer;
                    break;

                case MicrocodeBank.RAM2:
                    view = _ram2SourceViewer;
                    break;

                default:
                    throw new InvalidOperationException("Bank does not have a disassembly view.");
            }

            // Set/unset the breakpoint
            bool value = (bool)view.Rows[index].Cells[0].Value;
            view.Rows[index].Cells[0].Value = !value;

            ModifyMicrocodeBreakpoint(bank, (UInt16)index, !value);
        }

        private void MemoryViewCellClick(object sender, DataGridViewCellEventArgs e)
        {
            // Check for breakpoint column click.
            if (e.ColumnIndex == 0)
            {
                // Check/uncheck the box and set/unset a breakpoint for the line                
                bool value = (bool)_memoryData.Rows[e.RowIndex].Cells[0].Value;
                _memoryData.Rows[e.RowIndex].Cells[0].Value = !value;

                ModifyNovaBreakpoint((UInt16)e.RowIndex, !value);
            }
        }

        private void HighlightMicrocodeSourceLine(DataGridView view, UInt16 address)
        {
            foreach (DataGridViewRow row in view.Rows)
            {
                if (row.Tag != null &&
                    (ushort)(row.Tag) == address)
                {
                    view.ClearSelection();
                    row.Selected = true;
                    view.CurrentCell = row.Cells[0];
                    break;
                }
            }
        }

        private void HighlightMicrocodeDisassemblyLine(DataGridView view, UInt16 address)
        {
            DataGridViewRow row = view.Rows[address];
            
            view.ClearSelection();
            row.Selected = true;
            view.CurrentCell = row.Cells[0];            
        }

        private void HighlightNovaSourceLine(UInt16 address)
        {
            if (address < _memoryData.Rows.Count)
            {
                _memoryData.ClearSelection();
                _memoryData.Rows[address].Selected = true;
                _memoryData.CurrentCell = _memoryData.Rows[address].Cells[0];
            }
        }


        /// <summary>
        /// Fill in memory view on demand.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnMemoryCellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            // TODO: handle extended memory
            if (e.RowIndex > 65535)
            {
                // Top of memory, nothing to do
                return;
            }

            switch (_memoryData.Columns[e.ColumnIndex].Name)
            {
                case "Bkpt":
                    e.Value = GetNovaBreakpoint((UInt16)e.RowIndex);
                    break;

                case "Address":
                    e.Value = Conversion.ToOctal(e.RowIndex, 6);
                    break;

                case "Data":
                    e.Value = Conversion.ToOctal(_system.MemoryBus.DebugReadWord((ushort)e.RowIndex), 6);

                    break;

                case "Disassembly":
                    e.Value = CPU.Nova.NovaDisassembler.DisassembleInstruction(
                        (ushort)e.RowIndex,
                        _system.MemoryBus.DebugReadWord((ushort)e.RowIndex));
                    break;
            }

        }

        private void OnMicrocodeSourceValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex > 1024)
            {
                // Top of uCode memory, nothing to do.
                return;
            }

            DataGridView view = (DataGridView)sender;

            int bank = 0;
            switch ((string)view.Tag)
            {
                case "RAM0":
                    bank = 0;
                    break;

                case "RAM1":
                    bank = 1;
                    break;

                case "RAM2":
                    bank = 2;
                    break;

                default:
                    throw new InvalidOperationException("Invalid view Tag for disassembly view.");
            }

            ushort address = (ushort)(e.RowIndex + (bank * 1024));

            // Yes, switching on the Header Text seems clumsy and awful but this is
            // what WinForms has driven me to do.
            switch (((DataGridView)sender).Columns[e.ColumnIndex].HeaderText)
            {
                case "B":
                    e.Value = GetMicrocodeBreakpoint(MicrocodeBank.RAM0 + bank, (ushort)(address % 1024));
                    break;

                case "Addr":
                    e.Value = Conversion.ToOctal(e.RowIndex, 4);
                    break;

                case "Word":
                    e.Value = Conversion.ToOctal((int)UCodeMemory.UCodeRAM[address], 11);

                    break;

                case "Disassembly":
                    // TODO: should provide means to disassemble as specific task, not just Emulator.
                    MicroInstruction instruction = new MicroInstruction(UCodeMemory.UCodeRAM[address]);
                    e.Value = UCodeDisassembler.DisassembleInstruction(instruction, _system.CPU.CurrentTask.TaskType);
                    break;
            }
        }

        private void ModifyMicrocodeBreakpoint(MicrocodeBank bank, UInt16 address, bool set)
        {
            _microcodeBreakpointEnabled[(int)bank, address] = set;
        }

        private bool GetMicrocodeBreakpoint(MicrocodeBank bank, UInt16 address)
        {
            return _microcodeBreakpointEnabled[(int)bank, address];
        }

        private bool GetNovaBreakpoint(UInt16 address)
        {
            return _novaBreakpointEnabled[address];
        }

        private void ModifyNovaBreakpoint(UInt16 address, bool set)
        {
            _novaBreakpointEnabled[address] = set;
        }

        private string GetTextForTaskState(AltoCPU.Task task)
        {
            if (task == null)
            {
                return String.Empty;
            }
            else
            {
                // Wakeup bit
                string status = task.Wakeup ? "W" : String.Empty;

                // Run bit
                if (task.TaskType == _system.CPU.CurrentTask.TaskType)
                {
                    status += "R";
                }

                return status;
            }
        }

        private string GetTextForTaskBank(AltoCPU.Task task)
        {
            if (task == null)
            {
                return String.Empty;
            }
            else
            {
                return UCodeMemory.GetBank(task.TaskType).ToString();                
            }
        }

        private string GetTextForTask(TaskType task)
        {
            string[] taskText =
            {
                "EM",   // 0 - emulator
                "OR",   // 1 - orbit
                String.Empty,
                "TO",   // 3 - trident output
                "KS",   // 4 - disk sector
                String.Empty,
                String.Empty,
                "EN",   // 7 - ethernet
                "MR",   // 8 - memory refresh
                "DW",   // 9 - display word
                "CU",   // 10 - cursor
                "DH",   // 11 - display horizontal
                "DV",   // 12 - display vertical
                "PA",   // 13 - parity
                "KW",   // 14 - disk word
                "TI",   // 15 - trident input
            };

            if (task == TaskType.Invalid)
            {
                return String.Empty;
            }
            else
            {
                return taskText[(int)task];
            }
        }

        private Color GetColorForTask(TaskType task)
        {
            Color[] taskColors =
            {
                Color.LightBlue,    // 0 - emulator
                Color.LightGoldenrodYellow,    // 1 - orbit
                Color.LightGray,    // 2 - unused
                Color.LightCoral,   // 3 - trident output
                Color.LightGreen,   // 4 - disk sector
                Color.LightGray,    // 5 - unused
                Color.LightGray,    // 6 - unused
                Color.LightSalmon,  // 7 - ethernet
                Color.LightSeaGreen,// 8 - memory refresh
                Color.LightYellow,  // 9 - display word
                Color.LightPink,    // 10 - cursor
                Color.Chartreuse,   // 11 - display horizontal
                Color.LightCoral,   // 12 - display vertical
                Color.LightSteelBlue, // 13 - parity
                Color.Gray,         // 14 - disk word
                Color.LightSteelBlue, // 15 - trident output
            };

            if (task == TaskType.Invalid)
            {
                return Color.White;
            }
            else
            {
                return taskColors[(int)task];
            }
        }

        private struct SourceLine
        {
            public SourceLine(string sourceText)
            {
                //
                // Mangle "<-" found in the source into the unicode arrow character, just to be neat.
                //
                sourceText = sourceText.Replace("<-", _arrowChar.ToString());

                // See if line begins with something of the form "TNxxxxx>".
                // If it does then we have extra metadata to parse out.
                string[] tokens = sourceText.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                bool annotated = false;

                // Make the compiler happy
                Text = sourceText;
                Address = String.Empty;
                Task = TaskType.Invalid;

                if (tokens.Length > 0 &&
                    tokens[0].Length == 7 &&
                    tokens[0].EndsWith(">"))
                {
                    // Close enough.  Look for the task tag and parse out the (octal) address
                    switch (tokens[0].Substring(0, 2))
                    {
                        case "EM":
                            Task = TaskType.Emulator;
                            break;

                        case "OR":
                            Task = TaskType.Orbit;
                            break;

                        case "SE":
                            Task = TaskType.DiskSector;
                            break;

                        case "EN":
                            Task = TaskType.Ethernet;
                            break;

                        case "MR":
                            Task = TaskType.MemoryRefresh;
                            break;

                        case "DW":
                            Task = TaskType.DisplayWord;
                            break;

                        case "CU":
                            Task = TaskType.Cursor;
                            break;

                        case "DH":
                            Task = TaskType.DisplayHorizontal;
                            break;

                        case "DV":
                            Task = TaskType.DisplayVertical;
                            break;

                        case "PA":
                            Task = TaskType.Parity;
                            break;

                        case "KW":
                            Task = TaskType.DiskWord;
                            break;

                        case "XM":  //XMesa code, which runs in the Emulator task
                            Task = TaskType.Emulator;
                            break;

                        default:
                            Task = TaskType.Invalid;
                            break;
                    }

                    if (Task != TaskType.Invalid)
                    {
                        try
                        {
                            // Belongs to a task, so we can grab the address out as well
                            Address = sourceText.Substring(2, 4);
                        }
                        catch
                        {
                            // That didn't work for whatever reason, just treat this as a normal source line.
                            annotated = false;
                        }

                        Text = sourceText.Substring(tokens[0].Length + 1, sourceText.Length - tokens[0].Length - 1);
                        annotated = true;
                    }
                    else
                    {
                        // We will just display this as a non-source line
                        annotated = false;
                    }
                }

                if (!annotated)
                {
                    Text = sourceText;
                    Address = String.Empty;
                    Task = TaskType.Invalid;
                }
            }

            public string Text;
            public string Address;
            public TaskType Task;

        }

        private void OnTabChanged(object sender, EventArgs e)
        {
            RefreshMicrocodeDisassembly(_system.CPU.CurrentTask.MPC);
        }

        private void Debugger_Load(object sender, EventArgs e)
        {

        }

        private void OnJumpAddressKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Return ||
                e.KeyCode == Keys.Enter)
            {
                try
                {
                    UInt16 address = Convert.ToUInt16(JumpToAddress.Text, 8);

                    // find the source address that matches this, if any.
                    RefreshMicrocodeDisassembly(address);
                }
                catch
                {
                    // eh, just do nothing for now
                }
            }
        }

        private void OnMemoryJumpAddressKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Return ||
                e.KeyCode == Keys.Enter)
            {
                try
                {
                    UInt16 address = Convert.ToUInt16(MemoryJumpToAddress.Text, 8);

                    // find the source address that matches this, if any.
                    HighlightNovaSourceLine(address);
                }
                catch
                {
                    // eh, just do nothing for now
                }
            }
        }

        private void OnMemoryFindKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Return ||
               e.KeyCode == Keys.Enter)
            {
                try
                {
                    UInt16 value = Convert.ToUInt16(MemoryFindTextBox.Text, 8);

                    // find the first address that contains this value
                    ushort address = 0;
                    if (_memoryData.SelectedRows.Count > 0)
                    {
                        // start at entry after selected line
                        address = (ushort)(_memoryData.SelectedRows[0].Index + 1);
                    }

                    for(int i=address;i<65536;i++)
                    {
                        if (_system.MemoryBus.DebugReadWord((ushort)i) == value)
                        {
                            HighlightNovaSourceLine((ushort)i);
                            break;
                        }
                    }                    
                }
                catch
                {
                    // eh, just do nothing for now
                }
            }
        }

        private void OnStepButtonClicked(object sender, EventArgs e)
        {
            _execType = ExecutionType.Step;
            SetExecutionState(ExecutionState.SingleStep);
            _controller.StartExecution(AlternateBootType.None);
        }

        private void OnAutoStepButtonClicked(object sender, EventArgs e)
        {
            //
            // Continuously step (and update the UI)
            // until the "Stop" button is pressed or something bad happens.
            //
            _execType = ExecutionType.Auto;
            SetExecutionState(ExecutionState.AutoStep);
            _controller.StartExecution(AlternateBootType.None);
        }

        private void RunButton_Click(object sender, EventArgs e)
        {
            //
            // Continuously execute, but do not update UI
            // until the "Stop" button is pressed or something bad happens.
            //                  
            _execType = ExecutionType.Normal;
            SetExecutionState(ExecutionState.Running);
            _controller.StartExecution(AlternateBootType.None);
        }

        private void RunToNextTaskButton_Click(object sender, EventArgs e)
        {
            _execType = ExecutionType.NextTask;
            SetExecutionState(ExecutionState.Running);
            _controller.StartExecution(AlternateBootType.None);
        }

        /// <summary>
        /// Runs microcode until next Nova instruction is started
        /// This is done by simply breaking whenever the uPC for the emulator
        /// task returns to 20(octal) -- this is the restart point for the emulator
        /// task.        
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void NovaStep_Click(object sender, EventArgs e)
        {
            _execType = ExecutionType.NextNovaInstruction;
            SetExecutionState(ExecutionState.Running);
            _controller.StartExecution(AlternateBootType.None);

        }

        private void OnStopButtonClicked(object sender, EventArgs e)
        {
            _controller.StopExecution();
            Refresh();
        }


        private void ResetButton_Click(object sender, EventArgs e)
        {
            _controller.Reset(AlternateBootType.None);
            Refresh();
        }

        private void OnExecutionError(Exception e)
        {
            _lastExceptionText = e.Message;
            SetExecutionState(ExecutionState.InternalError);
        }

        private bool OnExecutionStep()
        {
            switch (_execType)
            {
                case ExecutionType.Auto:
                    {
                        // Execute a single step, then update UI and 
                        // sleep to give messages time to run.
                        this.BeginInvoke(new StepDelegate(RefreshUI));
                        this.BeginInvoke(new StepDelegate(Invalidate));
                        System.Threading.Thread.Sleep(10);
                        return false; /* break always */
                    }

                case ExecutionType.Step:
                    return true;  /* break always */

                case ExecutionType.Normal:
                case ExecutionType.NextTask:
                case ExecutionType.NextNovaInstruction:
                    // See if we need to stop here
                    if (_execAbort ||                                               // The Stop button was hit
                        _system.CPU.InternalBreak ||                                // Something internal has requested a debugger break
                        _microcodeBreakpointEnabled[
                            (int)UCodeMemory.GetBank(
                                _system.CPU.CurrentTask.TaskType), 
                                _system.CPU.CurrentTask.MPC] ||                     // A microcode breakpoint was hit
                        (_execType == ExecutionType.NextTask &&
                            _system.CPU.NextTask != null &&
                            _system.CPU.NextTask != _system.CPU.CurrentTask) ||     // The next task was switched to                    
                        (_system.CPU.CurrentTask.MPC == 0x10 &&                     // MPC is 20(octal) meaning a new Nova instruction and...
                            (_novaBreakpointEnabled[_system.CPU.R[6]] ||            // A breakpoint is set here
                             _execType == ExecutionType.NextNovaInstruction)))      // or we're running only a single Nova instruction.                              
                    {
                        if (!_execAbort)
                        {
                            SetExecutionState(ExecutionState.BreakpointStop);
                        }

                        // Stop here as we've hit a breakpoint or have been stopped 
                        // Update UI to indicate where we stopped.
                        this.BeginInvoke(new StepDelegate(RefreshUI));
                        this.BeginInvoke(new StepDelegate(Invalidate));

                        _system.CPU.InternalBreak = false;                       

                        _execAbort = false;
                        return true;
                    }

                    break;
            }

            return false;
        }


        private void SetExecutionState(ExecutionState state)
        {
            _execState = state;
            this.BeginInvoke(new StepDelegate(RefreshUI));
        }

        private enum ExecutionType
        {
            None = 0,
            Step,
            Auto,
            Normal,
            NextTask,
            NextNovaInstruction,
        }

        private enum ExecutionState
        {
            Stopped = 0,
            SingleStep,
            AutoStep,
            Running,
            BreakpointStop,
            InternalError,
        }

        private struct ReservedMemoryEntry
        {
            public ReservedMemoryEntry(ushort address, string name)
            {
                Address = address;
                Name = name;
            }

            public ushort Address;
            public string Name;
        }

        private ReservedMemoryEntry[] _reservedMemoryEntries =
        {
            new ReservedMemoryEntry(0x110, "DASTART"),
            new ReservedMemoryEntry(0x111, "V.INT"),
            new ReservedMemoryEntry(0x112, "ITQUAN"),
            new ReservedMemoryEntry(0x113, "ITBITS"),
            new ReservedMemoryEntry(0x114, "MOUSEX"),
            new ReservedMemoryEntry(0x115, "MOUSEY"),
            new ReservedMemoryEntry(0x116, "CURSORX"),
            new ReservedMemoryEntry(0x117, "CURSORY"),
            new ReservedMemoryEntry(0x118, "RTC"),
            new ReservedMemoryEntry(0x119, "CURMAP0"),
            new ReservedMemoryEntry(0x11a, "CURMAP1"),
            new ReservedMemoryEntry(0x11b, "CURMAP2"),
            new ReservedMemoryEntry(0x11c, "CURMAP3"),
            new ReservedMemoryEntry(0x11d, "CURMAP4"),
            new ReservedMemoryEntry(0x11e, "CURMAP5"),
            new ReservedMemoryEntry(0x11f, "CURMAP6"),
            new ReservedMemoryEntry(0x120, "CURMAP7"),
            new ReservedMemoryEntry(0x121, "CURMAP8"),
            new ReservedMemoryEntry(0x122, "CURMAP9"),
            new ReservedMemoryEntry(0x123, "CURMAP10"),
            new ReservedMemoryEntry(0x124, "CURMAP11"),
            new ReservedMemoryEntry(0x125, "CURMAP12"),
            new ReservedMemoryEntry(0x126, "CURMAP13"),
            new ReservedMemoryEntry(0x127, "CURMAP14"),
            new ReservedMemoryEntry(0x128, "CURMAP15"),
            new ReservedMemoryEntry(0x12a, "WW"),
            new ReservedMemoryEntry(0x12b, "ACTIVE"),
            new ReservedMemoryEntry(0x140, "PCLOC"),
            new ReservedMemoryEntry(0x141, "INTVEC0"),
            new ReservedMemoryEntry(0x142, "INTVEC1"),
            new ReservedMemoryEntry(0x143, "INTVEC2"),
            new ReservedMemoryEntry(0x144, "INTVEC3"),
            new ReservedMemoryEntry(0x145, "INTVEC4"),
            new ReservedMemoryEntry(0x146, "INTVEC5"),
            new ReservedMemoryEntry(0x147, "INTVEC6"),
            new ReservedMemoryEntry(0x148, "INTVEC7"),
            new ReservedMemoryEntry(0x149, "INTVEC8"),
            new ReservedMemoryEntry(0x14a, "INTVEC9"),
            new ReservedMemoryEntry(0x14b, "INTVEC10"),
            new ReservedMemoryEntry(0x14c, "INTVEC11"),
            new ReservedMemoryEntry(0x14d, "INTVEC12"),
            new ReservedMemoryEntry(0x14e, "INTVEC13"),
            new ReservedMemoryEntry(0x14f, "INTVEC14"),
            new ReservedMemoryEntry(0x151, "KBLK"),
            new ReservedMemoryEntry(0x152, "KSTAT"),
            new ReservedMemoryEntry(0x153, "KADDR"),
            new ReservedMemoryEntry(0x154, "S.INTBM"),
            new ReservedMemoryEntry(0x155, "ITTIM"),
            new ReservedMemoryEntry(0x156, "TRAPPC"),
            new ReservedMemoryEntry(0x180, "EPLOC"),
            new ReservedMemoryEntry(0x181, "EBLOC"),
            new ReservedMemoryEntry(0x182, "EELOC"),
            new ReservedMemoryEntry(0x183, "ELLOC"),
            new ReservedMemoryEntry(0x184, "EICLOC"),
            new ReservedMemoryEntry(0x185, "EIPLOC"),
            new ReservedMemoryEntry(0x186, "EOCLOC"),
            new ReservedMemoryEntry(0x187, "EOPLOC"),
            new ReservedMemoryEntry(0x188, "EHLOC"),
        };

        private AltoSystem _system;
        private ExecutionController _controller;

        // Unicode character for the Arrow used by Alto microcode
        private const char _arrowChar = (char)0x2190;

        // Execution / error state       
        private bool _execAbort;
        private ExecutionState _execState;
        private ExecutionType _execType;
        private string _lastExceptionText;

        private delegate void StepDelegate();

        // Microcode Debugger breakpoints; one entry per address since we only need
        // to worry about a 10 bit address space, this is fast and uses little memory.
        private bool[,] _microcodeBreakpointEnabled;

        // Nova Debugger breakpoints; same as above
        private bool[] _novaBreakpointEnabled;


    }
}
