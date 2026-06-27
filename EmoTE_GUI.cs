// ============================================================
//  EmoTE_GUI.cs  —  WinForms GUI for EmoTE_Extractor_v6
//  Compatible: C# 5 / .NET Framework 4.x
//  Build:
//    buildgui.bat
// ============================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

// ════════════════════════════════════════════════════════════
//  ENTRY POINT
// ════════════════════════════════════════════════════════════
class EmoTE_GUI_Entry
{
    [STAThread]
    static void Main()
    {
        try {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        } catch (Exception ex) {
            MessageBox.Show("Loi khoi dong:\n\n" + ex.ToString(), "EmoTE_GUI Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

// ════════════════════════════════════════════════════════════
//  DATA MODEL
// ════════════════════════════════════════════════════════════
class FileEntry
{
    public int    Index;
    public string Name;
    public string Type;
    public string SubTag;
    public uint   Offset;
    public uint   Size;
    public byte[] Data;
    public bool   IsModified;

    public override string ToString()
    {
        return string.Format("[{0:D4}] {1}  ({2}, {3:N0} B)", Index, Name, Type, Size);
    }
}

// ════════════════════════════════════════════════════════════
//  MAIN FORM
// ════════════════════════════════════════════════════════════
class MainForm : Form
{
    MenuStrip            menuBar;
    ToolStrip            toolBar;
    SplitContainer       splitH;
    TreeView             treeView;
    ListView             listDetail;
    RichTextBox          hexView;
    StatusStrip          statusBar;
    ToolStripStatusLabel lblStatus, lblCount;
    TabControl           tabRight;

    string          idxPath, iddPath;
    List<FileEntry> entries  = new List<FileEntry>();
    FileEntry       selected;
    bool            dirty;

    // ────────────────────────────────────────────────────────
    public MainForm()
    {
        Text          = "EmoTE / CharTE / fx Archive Editor";
        Size          = new Size(1200, 720);
        MinimumSize   = new Size(800, 500);
        StartPosition = FormStartPosition.CenterScreen;
        Font          = new Font("Segoe UI", 9f);
        Icon          = SystemIcons.Application;

        BuildMenu();
        BuildToolBar();
        BuildStatusBar();
        BuildLayout();
        ApplyTheme();

        SetStatus("San sang. Mo file IDX de bat dau.");
    }

    // ════════════════════════════════════════════════════════
    //  BUILD UI
    // ════════════════════════════════════════════════════════
    void BuildMenu()
    {
        menuBar = new MenuStrip();

        var mFile = new ToolStripMenuItem("&File");
        AddMenu(mFile, "&Mo IDX + IDD...",   Keys.Control | Keys.O, OnOpenFiles);
        AddMenu(mFile, "Luu IDD",            Keys.Control | Keys.S, OnSaveIdd);
        AddMenu(mFile, "Xuat tat ca...",     Keys.Control | Keys.E, OnExtractAll);
        mFile.DropDownItems.Add(new ToolStripSeparator());
        AddMenu(mFile, "&Thoat",             Keys.Alt | Keys.F4,    OnExit);

        var mEdit = new ToolStripMenuItem("&Chinh sua");
        AddMenu(mEdit, "Them entry moi",     Keys.Control | Keys.N, OnAddEntry);
        AddMenu(mEdit, "Nhap file vao entry...", Keys.Control | Keys.I, OnImportFile);
        AddMenu(mEdit, "Xuat entry dang chon...", Keys.Control | Keys.X, OnExportEntry);
        mEdit.DropDownItems.Add(new ToolStripSeparator());
        AddMenu(mEdit, "Doi ten entry...",   Keys.F2,               OnRenameEntry);
        AddMenu(mEdit, "Xoa entry",          Keys.Delete,           OnDeleteEntry);

        var mView = new ToolStripMenuItem("&View");
        AddMenu(mView, "Lam moi TreeView",   Keys.F5,               OnRefresh);
        AddMenu(mView, "Mo rong tat ca",     Keys.None,             OnExpandAll);
        AddMenu(mView, "Thu gon tat ca",     Keys.None,             OnCollapseAll);

        menuBar.Items.Add(mFile);
        menuBar.Items.Add(mEdit);
        menuBar.Items.Add(mView);
        Controls.Add(menuBar);
        MainMenuStrip = menuBar;
    }

    static void AddMenu(ToolStripMenuItem parent, string text, Keys keys, EventHandler handler)
    {
        var item = new ToolStripMenuItem(text);
        if (keys != Keys.None) item.ShortcutKeys = keys;
        item.Click += handler;
        parent.DropDownItems.Add(item);
    }

    ToolStripButton MakeBtn(string label, string tip, EventHandler handler)
    {
        var b = new ToolStripButton(label);
        b.ToolTipText    = tip;
        b.DisplayStyle   = ToolStripItemDisplayStyle.Text;
        b.Click         += handler;
        return b;
    }

    void BuildToolBar()
    {
        toolBar = new ToolStrip();
        toolBar.GripStyle = ToolStripGripStyle.Hidden;

        toolBar.Items.Add(MakeBtn("[Mo]",       "Mo IDX + IDD",         OnOpenFiles));
        toolBar.Items.Add(MakeBtn("[Luu]",      "Luu IDD",              OnSaveIdd));
        toolBar.Items.Add(new ToolStripSeparator());
        toolBar.Items.Add(MakeBtn("[+Them]",    "Them entry moi",       OnAddEntry));
        toolBar.Items.Add(MakeBtn("[Nhap]",     "Nhap file vao entry",  OnImportFile));
        toolBar.Items.Add(MakeBtn("[Xuat]",     "Xuat entry dang chon", OnExportEntry));
        toolBar.Items.Add(MakeBtn("[Doi ten]",  "Doi ten entry",        OnRenameEntry));
        toolBar.Items.Add(MakeBtn("[Xoa]",      "Xoa entry",            OnDeleteEntry));
        toolBar.Items.Add(new ToolStripSeparator());
        toolBar.Items.Add(MakeBtn("[Xuat tat]", "Xuat tat ca entry",    OnExtractAll));

        Controls.Add(toolBar);
    }

    void BuildStatusBar()
    {
        statusBar = new StatusStrip();
        lblStatus = new ToolStripStatusLabel("San sang");
        lblStatus.Spring    = true;
        lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        lblCount  = new ToolStripStatusLabel("0 entries");
        statusBar.Items.Add(lblStatus);
        statusBar.Items.Add(new ToolStripSeparator());
        statusBar.Items.Add(lblCount);
        Controls.Add(statusBar);
    }

    void BuildLayout()
    {
        splitH = new SplitContainer();
        splitH.Dock             = DockStyle.Fill;

        treeView = new TreeView();
        treeView.Dock          = DockStyle.Fill;
        treeView.ShowLines     = true;
        treeView.ShowPlusMinus = true;
        treeView.HideSelection = false;
        treeView.Font          = new Font("Consolas", 9f);
        treeView.AfterSelect          += OnTreeSelect;
        treeView.NodeMouseDoubleClick += OnTreeDblClick;
        treeView.KeyDown              += OnTreeKey;
        splitH.Panel1.Controls.Add(treeView);

        tabRight = new TabControl();
        tabRight.Dock = DockStyle.Fill;

        // Tab Details
        var tabDetails = new TabPage("Chi tiet");
        listDetail = new ListView();
        listDetail.Dock         = DockStyle.Fill;
        listDetail.View         = View.Details;
        listDetail.FullRowSelect= true;
        listDetail.GridLines    = true;
        listDetail.Font         = new Font("Consolas", 9f);
        listDetail.Columns.Add("Thuoc tinh", 160);
        listDetail.Columns.Add("Gia tri",    400);
        tabDetails.Controls.Add(listDetail);
        tabRight.TabPages.Add(tabDetails);

        // Tab Hex
        var tabHex = new TabPage("Hex View");
        hexView = new RichTextBox();
        hexView.Dock        = DockStyle.Fill;
        hexView.ReadOnly    = true;
        hexView.Font        = new Font("Consolas", 9f);
        hexView.BackColor   = Color.FromArgb(30, 30, 30);
        hexView.ForeColor   = Color.LightGreen;
        hexView.WordWrap    = false;
        hexView.ScrollBars  = RichTextBoxScrollBars.Both;
        tabHex.Controls.Add(hexView);
        tabRight.TabPages.Add(tabHex);

        splitH.Panel2.Controls.Add(tabRight);
        Controls.Add(splitH);
        splitH.Panel1MinSize = 180;
        splitH.Panel2MinSize = 400;
        Load += (s,e) => {
            int d = Math.Min(280, splitH.Width - splitH.Panel2MinSize - splitH.SplitterWidth - 10);
            if (d > splitH.Panel1MinSize) splitH.SplitterDistance = d;
        };
    }

    void ApplyTheme()
    {
        BackColor            = Color.FromArgb(45, 45, 48);
        ForeColor            = Color.FromArgb(220, 220, 220);
        treeView.BackColor   = Color.FromArgb(30, 30, 30);
        treeView.ForeColor   = Color.FromArgb(200, 200, 200);
        listDetail.BackColor = Color.FromArgb(37, 37, 38);
        listDetail.ForeColor = Color.FromArgb(220, 220, 220);
        menuBar.BackColor    = Color.FromArgb(45, 45, 48);
        menuBar.ForeColor    = Color.FromArgb(220, 220, 220);
        toolBar.BackColor    = Color.FromArgb(45, 45, 48);
        statusBar.BackColor  = Color.FromArgb(0, 122, 204);
        lblStatus.ForeColor  = Color.White;
        lblCount.ForeColor   = Color.White;
    }

    // ════════════════════════════════════════════════════════
    //  MENU / TOOLBAR EVENT STUBS
    // ════════════════════════════════════════════════════════
    void OnExit(object s, EventArgs e)      { Close(); }
    void OnRefresh(object s, EventArgs e)   { RebuildTree(); }
    void OnExpandAll(object s, EventArgs e) { treeView.ExpandAll(); }
    void OnCollapseAll(object s, EventArgs e){ treeView.CollapseAll(); }

    // ════════════════════════════════════════════════════════
    //  FILE OPERATIONS
    // ════════════════════════════════════════════════════════
    void OnOpenFiles(object s, EventArgs e)
    {
        if (dirty && !ConfirmDiscard()) return;

        var dlg = new OpenFileDialog();
        dlg.Title  = "Chon file IDX";
        dlg.Filter = "IDX files (*.idx)|*.idx|All files (*.*)|*.*";
        if (dlg.ShowDialog() != DialogResult.OK) return;

        string idx = dlg.FileName;
        string idd = Path.ChangeExtension(idx, ".idd");

        if (!File.Exists(idd))
        {
            var dlg2 = new OpenFileDialog();
            dlg2.Title    = "Chon file IDD";
            dlg2.Filter   = "IDD files (*.idd)|*.idd|All files (*.*)|*.*";
            dlg2.FileName = Path.GetFileNameWithoutExtension(idx) + ".idd";
            if (dlg2.ShowDialog() != DialogResult.OK) return;
            idd = dlg2.FileName;
        }

        LoadArchive(idx, idd);
    }

    void LoadArchive(string idx, string idd)
    {
        try
        {
            SetStatus(string.Format("Dang tai {0}...", Path.GetFileName(idx)));
            idxPath = idx;
            iddPath = idd;

            byte[] idxData = File.ReadAllBytes(idx);
            byte[] iddData = File.Exists(idd) ? File.ReadAllBytes(idd) : new byte[0];

            entries = ParseIdx(idxData, iddData);
            dirty   = false;
            RebuildTree();
            UpdateCount();
            SetStatus(string.Format("Da tai: {0}  -  {1} entries", Path.GetFileName(idx), entries.Count));
            Text = string.Format("EmoTE Archive Editor  [{0}]", Path.GetFileName(idx));
        }
        catch (Exception ex)
        {
            Err("Loi tai file", ex.Message);
            SetStatus("Loi tai file.");
        }
    }

    List<FileEntry> ParseIdx(byte[] raw, byte[] idd)
    {
        var list = new List<FileEntry>();

        bool isCharTE = false, isEmoTE = false;
        if (raw.Length > 47 + 55)
        {
            isCharTE = raw[47+24] == 0x62 && raw[47+25] == 0x76
                    && raw[47+26] == 0x61 && raw[47+27] == 0x74;
        }
        if (!isCharTE && raw.Length > 48 + 56)
        {
            isEmoTE  = raw[48+24] == 0x67 && raw[48+25] == 0x68
                    && raw[48+26] == 0x61 && raw[48+27] == 0x72;
        }

        int    hdr     = isCharTE ? 47 : isEmoTE ? 48 : 0;
        int    rsz     = isCharTE ? 55 : isEmoTE ? 56 : 0;
        string variant = isCharTE ? "CharTE" : isEmoTE ? "EmoTE" : "Fx";

        if (rsz > 0)
        {
            int n = (raw.Length - hdr) / rsz;
        if (n > 100000) { SetStatus("IDX bi hong: qua nhieu entries (" + n + ")"); return list; }
            for (int i = 0; i < n; i++)
            {
                int  b    = hdr + i * rsz;
                uint tf   = BitConverter.ToUInt32(raw, b);
                var  st   = new byte[4]; Array.Copy(raw, b+4,  st, 0, 4);
                uint ioff = BitConverter.ToUInt32(raw, b+8);
                uint f2   = BitConverter.ToUInt32(raw, b+12);
                var  kt   = new byte[4]; Array.Copy(raw, b+24, kt, 0, 4);

                string stStr = AsciiTag(st);
                string ktStr = AsciiTag(kt);
                string tfStr = tf == 0x1032070FU ? "ST" : tf == 0x173A0604U ? "IC"
                             : string.Format("0x{0:X8}", tf);
                string name  = string.Format("{0}_{1:D4}", ktStr, i);
                uint   size  = f2;

                byte[] data  = null;
                if (idd.Length > 0 && ioff < (uint)idd.Length
                    && size > 0 && size <= 64u*1024u*1024u
                    && ioff + size <= (uint)idd.Length)
                {
                    try { data = new byte[size]; Array.Copy(idd, ioff, data, 0, size); }
                    catch (OutOfMemoryException) { data = null; }
                }

                list.Add(new FileEntry {
                    Index  = i,
                    Name   = name,
                    Type   = variant + "/" + tfStr,
                    SubTag = stStr,
                    Offset = ioff,
                    Size   = size,
                    Data   = data,
                });
            }
        }
        else
        {
            // Fx scan
            const uint TF_ST = 0x1032070FU, TF_IC = 0x173A0604U;
            int idx2 = 0;
            for (int scan = 0; scan <= raw.Length - 24; scan += 4)
            {
                uint tf = BitConverter.ToUInt32(raw, scan);
                if (tf != TF_ST && tf != TF_IC) continue;
                uint ioff = BitConverter.ToUInt32(raw, scan+8);  if (ioff < 100) continue;
                uint f2   = BitConverter.ToUInt32(raw, scan+12); if (f2 == 0 || f2 > 64*1024*1024) continue;
                uint ksz  = BitConverter.ToUInt32(raw, scan+20); if (ksz == 0 || ksz > 256) continue;

                var  st    = new byte[4]; Array.Copy(raw, scan+4, st, 0, 4);
                string stStr = AsciiTag(st);
                string tfStr = tf == TF_ST ? "ST" : "IC";
                string name  = string.Format("{0}_{1:D4}", stStr, idx2);
                uint   size  = f2;

                byte[] data = null;
                if (idd.Length > 0 && ioff < (uint)idd.Length
                    && size > 0 && size <= 64u*1024u*1024u
                    && ioff + size <= (uint)idd.Length)
                {
                    try { data = new byte[size]; Array.Copy(idd, ioff, data, 0, size); }
                    catch (OutOfMemoryException) { data = null; }
                }

                list.Add(new FileEntry {
                    Index  = idx2++,
                    Name   = name,
                    Type   = "Fx/" + tfStr,
                    SubTag = stStr,
                    Offset = ioff,
                    Size   = size,
                    Data   = data,
                });
            }
        }

        return list;
    }

    static string AsciiTag(byte[] b)
    {
        foreach (byte x in b)
            if (x < 0x20 || x > 0x7E)
                return "0x" + BitConverter.ToString(b).Replace("-", "").ToLower();
        return Encoding.ASCII.GetString(b);
    }

    // ── Save IDD ────────────────────────────────────────────
    void OnSaveIdd(object s, EventArgs e)
    {
        if (iddPath == null)  { Info("Chua mo file IDD."); return; }
        if (!dirty)           { Info("Khong co thay doi nao."); return; }

        var dlg = new SaveFileDialog();
        dlg.Title    = "Luu IDD";
        dlg.Filter   = "IDD files (*.idd)|*.idd|All files (*.*)|*.*";
        dlg.FileName = Path.GetFileName(iddPath);
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            var ordered = entries.OrderBy(x => x.Offset).ToList();
            using (var ms = new MemoryStream())
            {
                foreach (var en in ordered)
                {
                    if (en.Data == null) continue;
                    while (ms.Length < en.Offset) ms.WriteByte(0);
                    ms.Write(en.Data, 0, en.Data.Length);
                }
                File.WriteAllBytes(dlg.FileName, ms.ToArray());
            }
            dirty = false;
            SetStatus(string.Format("Da luu: {0}", dlg.FileName));
        }
        catch (Exception ex) { Err("Loi luu", ex.Message); }
    }

    // ── Extract all ─────────────────────────────────────────
    void OnExtractAll(object s, EventArgs e)
    {
        if (entries.Count == 0) { Info("Chua co entry nao."); return; }

        var dlg = new FolderBrowserDialog();
        dlg.Description = "Chon thu muc xuat";
        if (dlg.ShowDialog() != DialogResult.OK) return;

        int ok = 0, fail = 0;
        foreach (var en in entries)
        {
            if (en.Data == null || en.Data.Length == 0) { fail++; continue; }
            try
            {
                string ext  = GuessExt(en.Data);
                string path = Path.Combine(dlg.SelectedPath,
                    string.Format("{0:D4}_{1}{2}", en.Index, en.Name, ext));
                File.WriteAllBytes(path, en.Data);
                ok++;
            }
            catch { fail++; }
        }
        SetStatus(string.Format("Xuat xong: {0} thanh cong, {1} loi.", ok, fail));
        Info(string.Format("Xuat xong!\n{0} file thanh cong.\n{1} file loi.", ok, fail));
    }

    static string GuessExt(byte[] d)
    {
        if (d.Length < 4) return ".bin";
        if (d[0]==0x4F && d[1]==0x67 && d[2]==0x67 && d[3]==0x53) return ".ogg";
        if (d[0]==0x89 && d[1]==0x50 && d[2]==0x4E && d[3]==0x47) return ".png";
        if (d[0]==0xFF && d[1]==0xD8 && d[2]==0xFF)                return ".jpg";
        if (d[0]==0x42 && d[1]==0x4D)                              return ".bmp";
        if (d[0]==0x1F && d[1]==0x8B)                              return ".gz";
        if (d[0]==0x50 && d[1]==0x4B && d[2]==0x03 && d[3]==0x04) return ".zip";
        return ".bin";
    }

    // ════════════════════════════════════════════════════════
    //  ENTRY OPERATIONS
    // ════════════════════════════════════════════════════════
    void OnAddEntry(object s, EventArgs e)
    {
        var dlg = new AddEntryDialog();
        if (dlg.ShowDialog() != DialogResult.OK) return;

        uint nextOffset = 0;
        if (entries.Count > 0)
            nextOffset = entries.Max(x => x.Offset + x.Size) + 16;

        var en = new FileEntry {
            Index      = entries.Count,
            Name       = dlg.EntryName,
            Type       = dlg.EntryType,
            SubTag     = "new_",
            Offset     = nextOffset,
            Size       = 0,
            Data       = new byte[0],
            IsModified = true,
        };
        entries.Add(en);
        dirty = true;
        RebuildTree();
        UpdateCount();
        SetStatus(string.Format("Da them entry: {0}", en.Name));
    }

    void OnImportFile(object s, EventArgs e)
    {
        if (selected == null) { Info("Chon mot entry truoc."); return; }

        var dlg = new OpenFileDialog();
        dlg.Title  = "Chon file de nhap vao entry";
        dlg.Filter = "All files (*.*)|*.*";
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            selected.Data       = File.ReadAllBytes(dlg.FileName);
            selected.Size       = (uint)selected.Data.Length;
            selected.IsModified = true;
            dirty = true;
            ShowEntryDetail(selected);
            RefreshTreeNode(selected);
            SetStatus(string.Format("Da nhap: {0}  ->  entry [{1}]",
                dlg.FileName, selected.Index));
        }
        catch (Exception ex) { Err("Loi nhap file", ex.Message); }
    }

    void OnExportEntry(object s, EventArgs e)
    {
        if (selected == null) { Info("Chon mot entry truoc."); return; }
        if (selected.Data == null || selected.Data.Length == 0)
        { Info("Entry nay khong co du lieu."); return; }

        string ext = GuessExt(selected.Data);
        var dlg = new SaveFileDialog();
        dlg.Title    = "Xuat entry";
        dlg.FileName = string.Format("{0:D4}_{1}{2}", selected.Index, selected.Name, ext);
        dlg.Filter   = "All files (*.*)|*.*";
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            File.WriteAllBytes(dlg.FileName, selected.Data);
            SetStatus(string.Format("Da xuat: {0}", dlg.FileName));
        }
        catch (Exception ex) { Err("Loi xuat", ex.Message); }
    }

    void OnRenameEntry(object s, EventArgs e)
    {
        if (selected == null) { Info("Chon mot entry truoc."); return; }

        var dlg = new InputDialog("Doi ten entry", "Ten moi:", selected.Name);
        if (dlg.ShowDialog() != DialogResult.OK) return;
        if (string.IsNullOrEmpty(dlg.Value)) return;

        selected.Name       = dlg.Value.Trim();
        selected.IsModified = true;
        dirty = true;
        RefreshTreeNode(selected);
        ShowEntryDetail(selected);
        SetStatus(string.Format("Da doi ten entry [{0}] -> {1}", selected.Index, selected.Name));
    }

    void OnDeleteEntry(object s, EventArgs e)
    {
        if (selected == null) return;

        if (MessageBox.Show(
            string.Format("Xoa entry [{0}] \"{1}\"?", selected.Index, selected.Name),
            "Xac nhan xoa", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        entries.Remove(selected);
        for (int i = 0; i < entries.Count; i++) entries[i].Index = i;
        selected = null;
        dirty = true;
        RebuildTree();
        UpdateCount();
        listDetail.Items.Clear();
        hexView.Clear();
        SetStatus("Da xoa entry.");
    }

    // ════════════════════════════════════════════════════════
    //  TREE VIEW
    // ════════════════════════════════════════════════════════
    void RebuildTree()
    {
        treeView.BeginUpdate();
        treeView.Nodes.Clear();

        var groups = new Dictionary<string, List<FileEntry>>();
        foreach (var en in entries)
        {
            string key = en.Type.Split('/')[0];
            if (!groups.ContainsKey(key))
                groups[key] = new List<FileEntry>();
            groups[key].Add(en);
        }

        foreach (var kv in groups)
        {
            var grpNode = new TreeNode(
                string.Format("[{0}]  ({1} entries)", kv.Key, kv.Value.Count));
            grpNode.ForeColor = Color.FromArgb(86, 156, 214);
            grpNode.NodeFont  = new Font("Segoe UI", 9f, FontStyle.Bold);

            foreach (var en in kv.Value)
            {
                string label = string.Format("[{0:D4}]  {1}", en.Index, en.Name);
                if (en.IsModified) label += "  *";
                var node = new TreeNode(label);
                node.Tag       = en;
                node.ForeColor = en.IsModified
                    ? Color.FromArgb(255, 215, 0)
                    : Color.FromArgb(200, 200, 200);
                grpNode.Nodes.Add(node);
            }
            treeView.Nodes.Add(grpNode);
        }

        treeView.ExpandAll();
        treeView.EndUpdate();
    }

    void RefreshTreeNode(FileEntry en)
    {
        foreach (TreeNode grp in treeView.Nodes)
        {
            foreach (TreeNode node in grp.Nodes)
            {
                if (node.Tag == en)
                {
                    string label = string.Format("[{0:D4}]  {1}", en.Index, en.Name);
                    if (en.IsModified) label += "  *";
                    node.Text      = label;
                    node.ForeColor = en.IsModified
                        ? Color.FromArgb(255, 215, 0)
                        : Color.FromArgb(200, 200, 200);
                    return;
                }
            }
        }
    }

    void OnTreeSelect(object s, TreeViewEventArgs e)
    {
        var en = e.Node != null ? e.Node.Tag as FileEntry : null;
        if (en != null)
        {
            selected = en;
            ShowEntryDetail(en);
        }
    }

    void OnTreeDblClick(object s, TreeNodeMouseClickEventArgs e)
    {
        var en = e.Node != null ? e.Node.Tag as FileEntry : null;
        if (en != null && en.Data != null && en.Data.Length > 0)
            OnExportEntry(s, EventArgs.Empty);
    }

    void OnTreeKey(object s, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete) OnDeleteEntry(s, EventArgs.Empty);
        if (e.KeyCode == Keys.F2)     OnRenameEntry(s, EventArgs.Empty);
    }

    // ════════════════════════════════════════════════════════
    //  DETAIL PANEL
    // ════════════════════════════════════════════════════════
    void ShowEntryDetail(FileEntry en)
    {
        listDetail.Items.Clear();
        Row("Index",     en.Index.ToString());
        Row("Ten",       en.Name);
        Row("Loai",      en.Type);
        Row("SubTag",    en.SubTag);
        Row("Offset",    string.Format("0x{0:X8}  ({1:N0})", en.Offset, en.Offset));
        Row("Kich thuoc",string.Format("{0:N0} bytes  ({1})", en.Size, FormatSize(en.Size)));
        Row("Du lieu",   en.Data != null
            ? string.Format("{0:N0} bytes da tai", en.Data.Length)
            : "khong co du lieu");
        Row("Da sua",    en.IsModified ? "Co (*)" : "Khong");
        if (en.Data != null && en.Data.Length >= 4)
            Row("Magic", GuessExt(en.Data).TrimStart('.').ToUpper());

        if (en.Data != null && en.Data.Length > 0)
        {
            int maxBytes = Math.Min(en.Data.Length, 512);
            hexView.Text = HexDump(en.Data, maxBytes);
            if (en.Data.Length > 512)
                hexView.AppendText(string.Format("\n... (+{0:N0} bytes)", en.Data.Length - 512));
        }
        else
        {
            hexView.Text = "(khong co du lieu)";
        }
    }

    void Row(string key, string val)
    {
        var item = new ListViewItem(key);
        item.SubItems.Add(val);
        listDetail.Items.Add(item);
    }

    static string HexDump(byte[] data, int count)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i += 16)
        {
            sb.Append(string.Format("{0:X6}  ", i));
            for (int j = 0; j < 16; j++)
            {
                if (i+j < count) sb.Append(string.Format("{0:X2} ", data[i+j]));
                else             sb.Append("   ");
                if (j == 7) sb.Append(' ');
            }
            sb.Append("  |");
            for (int j = 0; j < 16 && i+j < count; j++)
            {
                byte b = data[i+j];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }
            sb.AppendLine("|");
        }
        return sb.ToString();
    }

    static string FormatSize(uint n)
    {
        if (n < 1024)       return n + " B";
        if (n < 1024*1024)  return (n / 1024f).ToString("F1") + " KB";
        return (n / (1024f * 1024f)).ToString("F2") + " MB";
    }

    // ════════════════════════════════════════════════════════
    //  HELPERS
    // ════════════════════════════════════════════════════════
    void SetStatus(string msg)  { lblStatus.Text = msg; }
    void UpdateCount()          { lblCount.Text = string.Format("{0} entries", entries.Count); }
    void Info(string msg)       { MessageBox.Show(msg, "Thong bao", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    void Err(string t, string m){ MessageBox.Show(m, t, MessageBoxButtons.OK, MessageBoxIcon.Error); }

    bool ConfirmDiscard()
    {
        return MessageBox.Show("File dang co thay doi chua luu. Tiep tuc?",
            "Xac nhan", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (dirty && !ConfirmDiscard()) e.Cancel = true;
        base.OnFormClosing(e);
    }
}

// ════════════════════════════════════════════════════════════
//  DIALOG: Add Entry
// ════════════════════════════════════════════════════════════
class AddEntryDialog : Form
{
    TextBox  txtName;
    ComboBox cmbType;

    public string EntryName { get { return txtName.Text.Trim(); } }
    public string EntryType { get { return cmbType.Text; } }

    public AddEntryDialog()
    {
        Text            = "Them entry moi";
        Size            = new Size(360, 210);
        StartPosition   = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        Font            = new Font("Segoe UI", 9f);

        var lbl1 = new Label();  lbl1.Text = "Ten entry:"; lbl1.Location = new Point(12, 20); lbl1.AutoSize = true;
        txtName  = new TextBox(); txtName.Location = new Point(12, 40); txtName.Width = 316; txtName.Text = "new_entry";

        var lbl2 = new Label();  lbl2.Text = "Loai:";     lbl2.Location = new Point(12, 80); lbl2.AutoSize = true;
        cmbType  = new ComboBox();
        cmbType.Location      = new Point(12, 100);
        cmbType.Width         = 200;
        cmbType.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbType.Items.AddRange(new object[]{ "EmoTE/ST","EmoTE/IC","CharTE/ST","CharTE/IC","Fx/ST","Fx/IC" });
        cmbType.SelectedIndex = 0;

        var btnOK  = new Button(); btnOK.Text = "OK";   btnOK.Location = new Point(180, 140); btnOK.Width = 70; btnOK.DialogResult = DialogResult.OK;
        var btnCan = new Button(); btnCan.Text= "Huy";  btnCan.Location= new Point(258, 140); btnCan.Width= 70; btnCan.DialogResult= DialogResult.Cancel;

        AcceptButton = btnOK;
        CancelButton = btnCan;
        Controls.AddRange(new Control[]{ lbl1, txtName, lbl2, cmbType, btnOK, btnCan });
    }
}

// ════════════════════════════════════════════════════════════
//  DIALOG: Generic Text Input
// ════════════════════════════════════════════════════════════
class InputDialog : Form
{
    TextBox txt;
    public string Value { get { return txt.Text; } }

    public InputDialog(string title, string prompt, string initial)
    {
        Text            = title;
        Size            = new Size(360, 160);
        StartPosition   = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        Font            = new Font("Segoe UI", 9f);

        var lbl = new Label();   lbl.Text = prompt;   lbl.Location = new Point(12, 15); lbl.AutoSize = true;
        txt     = new TextBox(); txt.Text = initial;   txt.Location = new Point(12, 40); txt.Width = 316;

        var btnOK  = new Button(); btnOK.Text = "OK";  btnOK.Location = new Point(180, 85); btnOK.Width = 70; btnOK.DialogResult = DialogResult.OK;
        var btnCan = new Button(); btnCan.Text= "Huy"; btnCan.Location= new Point(258, 85); btnCan.Width= 70; btnCan.DialogResult= DialogResult.Cancel;

        AcceptButton = btnOK;
        CancelButton = btnCan;
        Controls.AddRange(new Control[]{ lbl, txt, btnOK, btnCan });
        Load += OnLoad;
    }

    void OnLoad(object s, EventArgs e)
    {
        txt.Focus();
        txt.SelectAll();
    }
}
