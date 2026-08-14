using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitLogger;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;
using static System.Net.Mime.MediaTypeNames;
using Color = System.Drawing.Color;
using ComboBox = System.Windows.Forms.ComboBox;
using Form = System.Windows.Forms.Form;
using Label = System.Windows.Forms.Label;

namespace ParameterTransfer
{
    /// <summary>
    /// Форма для выбора исходного и целевого параметров с автодополнением по вхождению.
    /// </summary>
    public class ParameterTransferForm : Form
    {
        private readonly List<string> _allParameterNames;

        public ComboBox CmbSource { get; private set; }
        public ComboBox CmbTarget { get; private set; }
        public CheckBox ChkOverwrite { get; private set; }
        public Button BtnRun { get; private set; }
        public Button BtnCancel { get; private set; }
        private Label _statusLabel;

        public string SourceParameterName => CmbSource.Text?.Trim();
        public string TargetParameterName => CmbTarget.Text?.Trim();
        public bool OverwriteExisting => ChkOverwrite.Checked;

        private bool _isFiltering = false;
        public ParameterTransferForm(List<string> parameterNames)
        {
            _allParameterNames = parameterNames ?? new List<string>();
            SetupForm();
            SetupControls();
        }

        private void SetupForm()
        {
            Text = "Перенос параметров";
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Width = 500;
            Height = 250;
        }

        private void SetupControls()
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 5,
                Padding = new Padding(10)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            layout.Controls.Add(MakeLabel("Исходный параметр:"), 0, 0);
            CmbSource = MakeAutoCompleteComboBox();
            layout.Controls.Add(CmbSource, 1, 0);

            layout.Controls.Add(MakeLabel("Целевой параметр:"), 0, 1);
            CmbTarget = MakeAutoCompleteComboBox();
            layout.Controls.Add(CmbTarget, 1, 1);

            ChkOverwrite = new CheckBox
            {
                Text = "Перезаписывать существующие значения",
                Checked = true,
                AutoSize = true,
                Margin = new Padding(3, 10, 3, 3)
            };
            layout.Controls.Add(ChkOverwrite, 0, 2);
            layout.SetColumnSpan(ChkOverwrite, 2);

            var buttonPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill,
                AutoSize = true
            };
            BtnCancel = new Button { Text = "✖ Отмена", Width = 110, DialogResult = DialogResult.Cancel };
            BtnRun = new Button { Text = "✅ Перенести", Width = 120 };
            BtnRun.Click += BtnRun_Click;
            buttonPanel.Controls.Add(BtnCancel);
            buttonPanel.Controls.Add(BtnRun);

            layout.Controls.Add(buttonPanel, 0, 3);
            layout.SetColumnSpan(buttonPanel, 2);

            _statusLabel = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 22,
                Padding = new Padding(5, 3, 0, 0),
                BackColor = Color.LightYellow,
                ForeColor = Color.DarkBlue,
                Text = $"Готово. Доступно параметров: {_allParameterNames.Count}"
            };

            Controls.Add(layout);
            Controls.Add(_statusLabel);

            AcceptButton = BtnRun;
            CancelButton = BtnCancel;
        }

        private ComboBox MakeAutoCompleteComboBox()
        {
            var cmb = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDown,
                Margin = new Padding(3)
            };
            // Отключаем стандартный AutoComplete, т.к. делаем собственную фильтрацию по вхождению
            cmb.AutoCompleteMode = AutoCompleteMode.None;
            cmb.Items.AddRange(_allParameterNames.ToArray());
            cmb.TextChanged += (s, e) => FilterComboBox(cmb);
            return cmb;
        }

        private Label MakeLabel(string text)
        {
            return new Label
            {
                Text = text,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
                AutoSize = false,
                Margin = new Padding(3)
            };
        }

        /// <summary>
        /// Фильтрует выпадающий список по вхождению подстроки.
        /// Список не открывается автоматически, чтобы не пропадал курсор мыши.
        /// </summary>
        private void FilterComboBox(ComboBox cmb)
        {
            if (_isFiltering) return;
            _isFiltering = true;

            try
            {
                // Сохраняем состояние ДО изменения коллекции
                string currentText = cmb.Text;
                int cursorPos = cmb.SelectionStart;

                cmb.BeginUpdate();
                cmb.Items.Clear();

                if (string.IsNullOrWhiteSpace(currentText))
                {
                    cmb.Items.AddRange(_allParameterNames.ToArray());
                }
                else
                {
                    var filtered = _allParameterNames
                        .Where(n => n.IndexOf(currentText, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToArray();
                    cmb.Items.AddRange(filtered);
                }

                cmb.EndUpdate();

                // Восстанавливаем текст и курсор ввода
                cmb.Text = currentText;
                cmb.SelectionStart = cursorPos;
                cmb.SelectionLength = 0;

                // Список обновлён, но НЕ открывается автоматически.
                // Пользователь откроет его кликом или стрелкой вниз.
                // Это полностью решает проблему исчезновения курсора мыши.
            }
            finally
            {
                _isFiltering = false;
            }
        }

        private void BtnRun_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(CmbSource.Text))
            {
                _statusLabel.Text = "Укажите исходный параметр";
                CmbSource.Focus();
                return;
            }
            if (string.IsNullOrWhiteSpace(CmbTarget.Text))
            {
                _statusLabel.Text = "Укажите целевой параметр";
                CmbTarget.Focus();
                return;
            }
            if (CmbSource.Text.Trim() == CmbTarget.Text.Trim())
            {
                _statusLabel.Text = "Параметры должны различаться";
                return;
            }

            _statusLabel.Text = $"Перенос: '{CmbSource.Text}' → '{CmbTarget.Text}'";
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
