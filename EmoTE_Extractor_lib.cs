// ============================================================
//  EmoTE / CharTE / fx Asset Extractor + Animation Decoder
//  PopTag / Crazy Arcade (Nexon) — v6
// ============================================================
//  Build:
//    csc EmoTE_Extractor_v6.cs /out:EmoTE_Extractor.exe
//    mcs EmoTE_Extractor_v6.cs /out:EmoTE_Extractor.exe
//
//  Commands:
//    EmoTE_Extractor.exe extract  <idx> <idd> <outdir>
//    EmoTE_Extractor.exe decode   <idx> <idd> <outdir> [skel] [mapinfo.ssd]
//    EmoTE_Extractor.exe dump     <idx>
//    EmoTE_Extractor.exe skel     <file.xml|.json|.skel>
//    EmoTE_Extractor.exe mapinfo  <fxdynmapinfo.ssd>
//
// ============================================================
//  AUTO-DETECTED FORMATS:
//    EmoTE  — KeyTag='ghar', header=48, record=56  (character animations)
//    CharTE — KeyTag='bvat', header=47, record=55  (character animations)
//    fx     — variable-length records, scan-based   (map effect animations)
//
//  SSD FORMAT (Nexon ssi1):
//    fxdynmapinfo.ssd — 284 map definitions (internal_id → display_name)
//    channel2_xxx.ssd — Server channel config
//    filterstr.ssd    — UI filter labels
//
//  OPCODE TABLE (IC/ST Q15 bytecode):
//    0x80..0x9F  channel separator  ch = byte - 0x80
//    0x42        emit 1 Q15 int16   (3 bytes)
//    0x43..0x7F  emit N Q15 int16s  N = byte-0x42  (EmoTE: up to 0x5F; CharTE/fx: up to 0x7F)
//    0x01..0x0F  emit 1 value + N-1 Bezier tangents  (2N+1 bytes)
//    0x1C        emit 1 Q15 int16   alt form  (3 bytes)
//    0x75        state instruction  (4 bytes)
//    0xE7        marker flag  (1 byte)
//    0xF3        instruction  (2 bytes)
//    0xFF        end-of-section  (1 byte)
//    0xFF 0xFF   end-of-block  (2 bytes)
//
//  CHARTE ST TABLE FORMAT:
//    [0]  num_entries uint32
//    [4]  version     uint32 = 2
//    [8]  total_bones uint32
//    [12] frame_count uint32
//    [16] entries: num_entries × { ch_type uint32, val_count uint32 }
//         ch_type: 0x00=inactive  0x17=rotation(deg)  0x19=position(px)  0x20=scale
//    data: val_count × int16 per active entry  (-1 = no-keyframe sentinel)
//
//  CHANNEL → BONE MAPPING:
//    bone_id = channel_id / 4
//    dof     = channel_id % 4  →  x y z w
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

class EmoTEExtractor
{
    // ── TypeFlags ───────────────────────────────────────────
    const uint TF_ST       = 0x1032070FU;
    const uint TF_IC       = 0x173A0604U;
    const uint TF_COMPOUND = 0x033F7C74U;
    const uint TF_CONF     = 0x666E6F63U;

    static readonly byte[] MAGIC_EMOTE = { 0xDA, 0xD9, 0x93, 0x7C };
    static readonly byte[] MAGIC_BTREE = { 0x02,0x00,0x00,0x00, 0x02,0x00,0x00,0x00 };
    static readonly byte[] MAGIC_OGG   = { 0x4F, 0x67, 0x67, 0x53 };
    static readonly byte[] MAGIC_SSD   = { 0x73, 0x73, 0x69, 0x31 }; // "ssi1"

    enum IdxVariant { EmoTE, CharTE, Fx }
    static IdxVariant g_Variant = IdxVariant.EmoTE;

    // ════════════════════════════════════════════════════════
    //  DATA STRUCTURES
    // ════════════════════════════════════════════════════════
    class IdxRecord
    {
        public int    Idx;
        public uint   TypeFlags;
        public byte[] SubTag   = new byte[4];
        public uint   IddOffset;
        public uint   Field2;
        public uint   Field3;
        public uint   KeySize;
        public byte[] KeyTag   = new byte[4];
        public byte[] KeyData  = new byte[0];

        public string SubTagStr
        {
            get
            {
                foreach (byte b in SubTag)
                    if (b < 0x20 || b > 0x7E)
                        return "0x" + ToHex(SubTag);
                return Encoding.ASCII.GetString(SubTag);
            }
        }
    }

    class AnimChannel
    {
        public int         Id;
        public List<float> Values   = new List<float>();
        public List<float> Tangents = new List<float>();
    }

    class AnimPair
    {
        public int    PairIndex;
        public string SubTag;
        public string StFormat;
        public string MapId;          // from fxdynmapinfo (internal ID)
        public string MapName;        // from fxdynmapinfo (display name)
        public Dictionary<int, AnimChannel> IC = new Dictionary<int, AnimChannel>();
        public Dictionary<int, AnimChannel> ST = new Dictionary<int, AnimChannel>();
    }

    class AnimPairRef { public IdxRecord ST; public IdxRecord IC; }

    // ── Map definition (from fxdynmapinfo.ssd) ──────────────
    class MapEntry
    {
        public int    Index;
        public string InternalId;
        public string DisplayName;
    }

    // ── Skeleton definition ──────────────────────────────────
    class SkeletonDef
    {
        public string   Name      = "";
        public string[] BoneNames = new string[0];
        public int[]    Parents;
        public float[]  RestX, RestY, RestAngle, RestScale;
        public bool     Loaded;
    }

    static SkeletonDef     g_Skel    = new SkeletonDef();
    static List<MapEntry>  g_MapTable = new List<MapEntry>();

    // ════════════════════════════════════════════════════════
    //  ENTRY POINT
    // ════════════════════════════════════════════════════════
    static void MainCLI(string[] args)
    {
        if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
        { PrintHelp(); return; }

        try
        {
            switch (args[0].ToLower())
            {
                case "extract":
                    Need(args, 4, "extract <idx> <idd> <outdir>");
                    Extract(args[1], args[2], args[3]);
                    break;

                case "decode":
                    Need(args, 4, "decode <idx> <idd> <outdir> [skel] [mapinfo.ssd]");
                    for (int a = 4; a < args.Length; a++)
                    {
                        string ext = Path.GetExtension(args[a]).ToLower();
                        if (ext == ".ssd") LoadSsd(args[a]);
                        else               LoadSkeleton(args[a]);
                    }
                    Decode(args[1], args[2], args[3]);
                    break;

                case "dump":
                    Need(args, 2, "dump <idx>");
                    Dump(args[1]);
                    break;

                case "skel":
                    Need(args, 2, "skel <file>");
                    LoadSkeleton(args[1]);
                    PrintSkeleton();
                    break;

                case "mapinfo":
                    Need(args, 2, "mapinfo <fxdynmapinfo.ssd>");
                    LoadSsd(args[1]);
                    PrintMapTable();
                    break;

                default: Die("Unknown command: " + args[0]); break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("\n[ERROR] " + ex.Message);
            Console.Error.WriteLine(ex.StackTrace);
            Environment.Exit(1);
        }
    }

    // ════════════════════════════════════════════════════════
    //  HELP
    // ════════════════════════════════════════════════════════
    static void PrintHelp()
    {
        Console.WriteLine("EmoTE/CharTE/fx Extractor v6 — PopTag / Crazy Arcade (Nexon)");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  extract <idx> <idd> <outdir>                    extract raw assets");
        Console.WriteLine("  decode  <idx> <idd> <outdir> [skel] [map.ssd]   decode animations");
        Console.WriteLine("  dump    <idx>                                    IDX metadata");
        Console.WriteLine("  skel    <file>                                   parse skeleton");
        Console.WriteLine("  mapinfo <fxdynmapinfo.ssd>                       list map table");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  EmoTE_Extractor.exe decode EmoTE.idx  EmoTE.idd  out/");
        Console.WriteLine("  EmoTE_Extractor.exe decode CharTE.idx CharTE.idd out/ skeleton.xml");
        Console.WriteLine("  EmoTE_Extractor.exe decode fx.idx     fx.idd     out/ fxdynmapinfo.ssd");
        Console.WriteLine("  EmoTE_Extractor.exe decode fx.idx     fx.idd     out/ skeleton.xml fxdynmapinfo.ssd");
        Console.WriteLine();
        Console.WriteLine("Auto-detected formats:");
        Console.WriteLine("  EmoTE  — KeyTag='ghar', hdr=48, rec=56  (character animations)");
        Console.WriteLine("  CharTE — KeyTag='bvat', hdr=47, rec=55  (character animations)");
        Console.WriteLine("  fx     — variable-length scan             (map effect animations)");
        Console.WriteLine();
        Console.WriteLine("SSD files (Nexon ssi1 format, ZLIB compressed):");
        Console.WriteLine("  fxdynmapinfo.ssd  — 284 map entries (internal_id → display_name)");
        Console.WriteLine("  channel2_xxx.ssd  — server channel config");
        Console.WriteLine("  filterstr.ssd     — UI filter labels");
    }

    // ════════════════════════════════════════════════════════
    //  SSD LOADER (Nexon ssi1 format)
    // ════════════════════════════════════════════════════════
    static void LoadSsd(string path)
    {
        if (!File.Exists(path)) { Console.WriteLine("SSD not found: " + path); return; }
        Console.Write("Loading SSD: " + path + " ... ");

        byte[] raw = File.ReadAllBytes(path);
        if (raw.Length < 18 || !BytesStartWith(raw, 0, MAGIC_SSD))
        { Console.WriteLine("not a ssi1 file"); return; }

        string fname = Path.GetFileNameWithoutExtension(path).ToLower();

        // Check for ZLIB at offset 18
        if (raw[18] == 0x78)
        {
            byte[] decompressed = ZlibDecompress(raw, 18, raw.Length - 18);
            if (decompressed == null) { Console.WriteLine("ZLIB decompress failed"); return; }

            if (fname.Contains("dynmapinfo") || fname.Contains("mapinfo"))
                LoadFxDynMapInfo(decompressed);
            else if (fname.Contains("channel"))
                LoadChannelInfo(decompressed);
            else
                Console.WriteLine("unknown SSD type");
        }
        else
        {
            // Raw string list (filterstr.ssd)
            LoadFilterStr(raw);
        }
    }

    // ── fxdynmapinfo.ssd ────────────────────────────────────
    static void LoadFxDynMapInfo(byte[] data)
    {
        g_MapTable.Clear();
        int pos = 0;
        if (pos + 2 > data.Length) return;
        int count = (int)BitConverter.ToUInt16(data, pos); pos += 2;

        while (pos + 8 < data.Length && g_MapTable.Count < count + 50)
        {
            // Search for valid (slen1, str1, slen2, str2) pattern
            bool found = false;
            for (int skip = 0; skip < 8 && pos + skip + 12 < data.Length; skip++)
            {
                int p = pos + skip;
                int s1l = (int)BitConverter.ToUInt32(data, p);
                if (s1l < 2 || s1l > 60 || p + 4 + s1l + 4 > data.Length) continue;
                if (!IsAsciiPrintable(data, p+4, s1l)) continue;
                string s1 = Encoding.ASCII.GetString(data, p+4, s1l);

                int p2 = p + 4 + s1l;
                int s2l = (int)BitConverter.ToUInt32(data, p2);
                if (s2l < 2 || s2l > 60 || p2 + 4 + s2l > data.Length) continue;
                if (!IsAsciiPrintable(data, p2+4, s2l)) continue;
                string s2 = Encoding.ASCII.GetString(data, p2+4, s2l);

                g_MapTable.Add(new MapEntry {
                    Index       = g_MapTable.Count,
                    InternalId  = s1,
                    DisplayName = s2
                });
                pos = p2 + 4 + s2l;
                found = true;
                break;
            }
            if (!found) pos++;
        }

        Console.WriteLine("OK (" + g_MapTable.Count + " map entries)");
    }

    // ── channel2_xxx.ssd ────────────────────────────────────
    static void LoadChannelInfo(byte[] data)
    {
        if (data.Length < 2) return;
        int count = (int)BitConverter.ToUInt16(data, 0);
        Console.WriteLine("OK (" + count + " server channels)");
        int pos = 2;
        for (int i = 0; i < count && pos + 4 < data.Length; i++)
        {
            int slen = (int)BitConverter.ToUInt32(data, pos); pos += 4;
            if (slen <= 0 || pos + slen > data.Length) break;
            string name = Encoding.GetEncoding("iso-8859-1").GetString(data, pos, slen); pos += slen;
            int maxSlots = (pos < data.Length) ? data[pos] : 0;
            pos += 26;
            Console.WriteLine("  Channel: " + name + " (max_slots=" + maxSlots + ")");
        }
    }

    // ── filterstr.ssd ────────────────────────────────────────
    static void LoadFilterStr(byte[] raw)
    {
        Console.WriteLine("OK (UI filter labels):");
        int pos = 16;
        while (pos + 2 <= raw.Length)
        {
            int slen = (int)BitConverter.ToUInt16(raw, pos); pos += 2;
            if (slen == 0 || pos + slen > raw.Length) break;
            Console.WriteLine("  " + Encoding.UTF8.GetString(raw, pos, slen));
            pos += slen;
        }
    }

    static void PrintMapTable()
    {
        Console.WriteLine("Map table (" + g_MapTable.Count + " entries):");
        Console.WriteLine(String.Format("{0,5}  {1,-28} {2}", "Idx", "InternalId", "DisplayName"));
        foreach (var m in g_MapTable)
            Console.WriteLine(String.Format("{0,5}  {1,-28} {2}", m.Index, m.InternalId, m.DisplayName));
    }

    // ── ZLIB decompress ──────────────────────────────────────
    static byte[] ZlibDecompress(byte[] data, int offset, int length)
    {
        try
        {
            // Skip 2-byte zlib header (0x78 0x??)
            using (var ms  = new MemoryStream(data, offset + 2, length - 2))
            using (var ds  = new DeflateStream(ms, CompressionMode.Decompress))
            using (var out2 = new MemoryStream())
            {
                ds.CopyTo(out2);
                return out2.ToArray();
            }
        }
        catch { return null; }
    }

    // ════════════════════════════════════════════════════════
    //  DUMP
    // ════════════════════════════════════════════════════════
    static void Dump(string idxPath)
    {
        var recs = LoadIdx(idxPath);
        Console.WriteLine("IDX   : " + idxPath);
        Console.WriteLine("Format: " + g_Variant);
        Console.WriteLine("Records: " + recs.Count);
        Console.WriteLine("\nTypeFlags frequency:");
        foreach (var g in recs.GroupBy(r => r.TypeFlags).OrderByDescending(g => g.Count()).Take(15))
        {
            byte[] b4 = BitConverter.GetBytes(g.Key);
            string asc = new string(b4.Select(b=>(b>=0x20&&b<=0x7E)?(char)b:'.').ToArray());
            string lbl = TfLabel(g.Key);
            Console.WriteLine("  0x"+g.Key.ToString("X8")+" '"+asc+"': "+g.Count()+lbl);
        }
        Console.WriteLine("\nFirst 20 records:");
        Console.WriteLine(String.Format("{0,6} {1,-10} {2,-14} {3,12} {4,8}","Idx","TypeFlags","SubTag","IddOffset","F2"));
        foreach (var r in recs.Take(20))
            Console.WriteLine(String.Format("{0,6} 0x{1,-8} {2,-14} {3,12} {4,8}",
                r.Idx,r.TypeFlags.ToString("X8"),r.SubTagStr,r.IddOffset,r.Field2));
    }

    static string TfLabel(uint tf)
    {
        if(tf==TF_ST)return" [ST]"; if(tf==TF_IC)return" [IC]";
        if(tf==TF_COMPOUND)return" [compound]"; if(tf==TF_CONF)return" [conf]";
        return "";
    }

    // ════════════════════════════════════════════════════════
    //  EXTRACT
    // ════════════════════════════════════════════════════════
    static void Extract(string idxPath, string iddPath, string outDir)
    {
        Banner("EmoTE/CharTE/fx v6 — Extract [" + g_Variant + "]");
        Console.Write("Loading... ");
        var recs = LoadIdx(idxPath);
        byte[] idd = File.ReadAllBytes(iddPath);
        Console.WriteLine(recs.Count + " records, " + FmtSize(idd.Length));

        var bounds = BuildBoundaries(recs, idd.Length);
        string dAnim  = MkDir(outDir,"animation");
        string dTbl   = MkDir(outDir,"asset_tables");
        string dAudio = MkDir(outDir,"audio_ogg");
        string dMisc  = MkDir(outDir,"misc");
        int cAnim=0,cTbl=0,cMisc=0,cSkip=0;

        var sorted = recs.Where(r=>r.IddOffset>100&&r.IddOffset<(uint)idd.Length)
                         .OrderBy(r=>r.IddOffset).ToList();

        using (var mapW = new StreamWriter(Path.Combine(outDir,"_idd_map.tsv"),false,Encoding.UTF8))
        using (var mfW  = new StreamWriter(Path.Combine(outDir,"_manifest.tsv"),false,Encoding.UTF8))
        {
            mfW.WriteLine("Idx\tTypeFlags\tSubTag\tIddOffset\tField2\tKeySize");
            foreach (var r in recs)
                mfW.WriteLine(r.Idx+"\t0x"+r.TypeFlags.ToString("X8")+"\t"+r.SubTagStr
                    +"\t"+r.IddOffset+"\t"+r.Field2+"\t"+r.KeySize);

            mapW.WriteLine("Idx\tTypeFlags\tSubTag\tIddOffset\tBlockSize\tContentType\tFileName");
            for (int j=0; j<sorted.Count; j++)
            {
                var r = sorted[j];
                int size = GetBlockSize(bounds, r, idd.Length);
                if (size<=0){cSkip++;continue;}
                int copyLen=Math.Min(size,idd.Length-(int)r.IddOffset);
                var block=new byte[copyLen];
                Array.Copy(idd,(int)r.IddOffset,block,0,copyLen);
                string ctype,fname,dir;
                ClassifyBlock(r,block,out ctype,out fname,out dir,dAnim,dTbl,dMisc);
                if(ctype.StartsWith("emote_anim"))cAnim++;
                else if(ctype=="emote_table")cTbl++;
                else cMisc++;
                File.WriteAllBytes(Path.Combine(dir,fname),block);
                mapW.WriteLine(r.Idx+"\t0x"+r.TypeFlags.ToString("X8")+"\t"
                    +r.SubTagStr+"\t"+r.IddOffset+"\t"+block.Length+"\t"+ctype+"\t"+fname);
                if(j%200==0||j==sorted.Count-1)Console.Write("\r  [{0}/{1}]   ",j+1,sorted.Count);
            }
        }
        Console.WriteLine();
        WriteAnimPairIndex(recs, outDir);
        Console.Write("  OGG... "); int cAudio=ExtractOgg(idd,dAudio);
        Console.WriteLine(cAudio+" files");
        Console.WriteLine("\n  .emote: "+cAnim+"  tables: "+cTbl+"  ogg: "+cAudio+"  misc: "+cMisc);
    }

    static void ClassifyBlock(IdxRecord r, byte[] block,
        out string ctype, out string fname, out string dir,
        string dAnim, string dTbl, string dMisc)
    {
        string tag = SafeName(r.SubTagStr,16);
        if(r.TypeFlags==TF_ST){ctype="emote_anim_st";dir=dAnim;fname="anim_"+r.Idx.ToString("D5")+"_st_"+tag+".emote";}
        else if(r.TypeFlags==TF_IC){ctype="emote_anim_ic";dir=dAnim;fname="anim_"+r.Idx.ToString("D5")+"_ic_"+tag+".emote";}
        else if(r.TypeFlags==TF_COMPOUND||BytesContain(block,MAGIC_EMOTE,64)){ctype="emote_table";dir=dTbl;fname="tbl_"+r.Idx.ToString("D5")+"_"+tag+".etbl";}
        else{ctype=DetectMagic(block);dir=dMisc;fname="misc_"+r.Idx.ToString("D5")+"_"+tag+ExtFor(ctype);}
    }

    // ════════════════════════════════════════════════════════
    //  DECODE
    // ════════════════════════════════════════════════════════
    static void Decode(string idxPath, string iddPath, string outDir)
    {
        Banner("EmoTE/CharTE/fx v6 — Decode [" + g_Variant + "]");
        Console.WriteLine("  IDX : " + idxPath);
        Console.WriteLine("  IDD : " + iddPath);
        Console.WriteLine("  Out : " + outDir);
        if(g_Skel.Loaded)    Console.WriteLine("  Skel: "+g_Skel.Name+" ("+g_Skel.BoneNames.Length+" bones)");
        if(g_MapTable.Count>0)Console.WriteLine("  Maps: "+g_MapTable.Count+" entries loaded");

        Console.Write("\nLoading... ");
        var recs = LoadIdx(idxPath);
        byte[] idd = File.ReadAllBytes(iddPath);
        Console.WriteLine(recs.Count+" records, "+FmtSize(idd.Length));

        var bounds = BuildBoundaries(recs, idd.Length);
        string animDir = MkDir(outDir,"animations");
        var pairs = FindAnimPairs(recs);
        Console.WriteLine("  "+pairs.Count+" ST+IC pairs\n");

        using (var idxW=new StreamWriter(Path.Combine(outDir,"_anim_index.tsv"),false,Encoding.UTF8))
        {
            idxW.WriteLine("PairIdx\tSubTag\tMapId\tMapName\tStFormat\tFile\tIC_ch\tST_ch\tIC_vals\tST_vals");
            int done=0;
            foreach (var pair in pairs)
            {
                var anim = DecodeAnimPair(pair.ST, pair.IC, idd, bounds);

                // Attach map name from sequential index
                if (g_Variant == IdxVariant.Fx && g_MapTable.Count > 0)
                {
                    int mi = anim.PairIndex < g_MapTable.Count ? anim.PairIndex : -1;
                    if (mi >= 0)
                    { anim.MapId = g_MapTable[mi].InternalId; anim.MapName = g_MapTable[mi].DisplayName; }
                }

                // Build filename: use map display name when available
                string nameForFile = !string.IsNullOrEmpty(anim.MapName) ? anim.MapName
                                   : !string.IsNullOrEmpty(anim.MapId)   ? anim.MapId
                                   : anim.SubTag;
                string tag   = SafeName(nameForFile, 30);
                string fname = "anim_"+anim.PairIndex.ToString("D5")+"_"+tag+".json";
                WriteAnimJson(anim, Path.Combine(animDir, fname));

                idxW.WriteLine(anim.PairIndex+"\t"+anim.SubTag+"\t"
                    +(anim.MapId??"") +"\t"+(anim.MapName??"")
                    +"\t"+anim.StFormat+"\t"+fname
                    +"\t"+anim.IC.Count+"\t"+anim.ST.Count
                    +"\t"+anim.IC.Values.Sum(c=>c.Values.Count)
                    +"\t"+anim.ST.Values.Sum(c=>c.Values.Count));

                done++;
                if(done%50==0||done==pairs.Count)
                    Console.Write("\r  [{0}/{1}] {2}",done,pairs.Count,fname.PadRight(48));
            }
        }
        Console.WriteLine();
        Console.WriteLine("\n  _anim_index.tsv  ("+pairs.Count+" entries)");
        Console.WriteLine("  animations/");
        if(!g_Skel.Loaded)   Console.WriteLine("\n  TIP: Load skeleton: decode ... skeleton.xml");
        if(g_MapTable.Count==0&&g_Variant==IdxVariant.Fx)
            Console.WriteLine("  TIP: Load map names: decode fx.idx fx.idd out/ fxdynmapinfo.ssd");
    }

    // ════════════════════════════════════════════════════════
    //  DECODE ONE PAIR
    // ════════════════════════════════════════════════════════
    static AnimPair DecodeAnimPair(IdxRecord stRec, IdxRecord icRec, byte[] idd, SortedSet<int> bounds)
    {
        var pair = new AnimPair{ PairIndex=stRec.Idx, SubTag=stRec.SubTagStr, StFormat="bytecode" };

        // IC
        int icOff=  (int)icRec.IddOffset;
        int icSize= GetBlockSize(bounds,icRec,idd.Length);
        if(icSize>0&&icOff+icSize<=idd.Length)
        {
            int mp=FindBytes(idd,MAGIC_EMOTE,icOff,Math.Min(icSize,4*1024*1024));
            int bcLen=mp>=0?mp-icOff:icSize;
            pair.IC=DecodeChannels(idd,icOff,bcLen);
        }

        // ST
        int stOff=  (int)stRec.IddOffset;
        int stSize= GetBlockSize(bounds,stRec,idd.Length);
        if(stSize>0&&stOff+stSize<=idd.Length)
        {
            if(g_Variant==IdxVariant.CharTE||g_Variant==IdxVariant.Fx)
            {
                var tbl=DecodeCharTeST(idd,stOff,stSize);
                if(tbl!=null){pair.ST=tbl;pair.StFormat="table";}
                else pair.ST=DecodeChannels(idd,stOff,stSize);
            }
            else
            {
                int bcStart=stOff,bcLen=stSize;
                if(BytesStartWith(idd,stOff,MAGIC_BTREE))
                {
                    int b4=FindByte(idd,0xB4,stOff,stSize);
                    if(b4>=0){int b4e=b4;while(b4e<stOff+stSize&&idd[b4e]==0xB4)b4e++;bcStart=b4e;bcLen=(stOff+stSize)-b4e;}
                }
                else{int mp=FindBytes(idd,MAGIC_EMOTE,stOff,Math.Min(stSize,4*1024*1024));if(mp>=0)bcLen=mp-stOff;}
                if(bcLen>0)pair.ST=DecodeChannels(idd,bcStart,bcLen);
            }
        }
        return pair;
    }

    // ════════════════════════════════════════════════════════
    //  Q15 BYTECODE DECODER
    // ════════════════════════════════════════════════════════
    static Dictionary<int,AnimChannel> DecodeChannels(byte[] data, int start, int length)
    {
        var chs=new Dictionary<int,AnimChannel>();
        AnimChannel cur=null; int end=start+length, i=start;
        while(i<end)
        {
            byte b=data[i];
            if(b>=0x80&&b<=0x9F){int id=b-0x80;if(!chs.ContainsKey(id))chs[id]=new AnimChannel{Id=id};cur=chs[id];i++;continue;}
            if(b==0x42){if(cur!=null&&i+2<end)cur.Values.Add(ReadQ15(data,i+1));i+=3;continue;}
            if(b>=0x43&&b<=0x7F){int n=b-0x42;i++;for(int k=0;k<n&&i+1<end;k++,i+=2)if(cur!=null)cur.Values.Add(ReadQ15(data,i));continue;}
            if(b>=0x01&&b<=0x0F){int nt=(int)b;i++;for(int k=0;k<nt&&i+1<end;k++,i+=2){float v=ReadQ15(data,i);if(cur!=null){if(k==0)cur.Values.Add(v);else cur.Tangents.Add(v);}}continue;}
            if(b==0x1C){if(cur!=null&&i+2<end)cur.Values.Add(ReadQ15(data,i+1));i+=3;continue;}
            if(b==0xFF){i+=(i+1<end&&data[i+1]==0xFF)?2:1;continue;}
            if(b==0x75){i+=4;continue;} if(b==0xF3){i+=2;continue;}
            i++;
        }
        return chs;
    }

    // ════════════════════════════════════════════════════════
    //  CHARTE / FX ST TABLE DECODER
    // ════════════════════════════════════════════════════════
    static Dictionary<int,AnimChannel> DecodeCharTeST(byte[] idd, int off, int size)
    {
        if(size<16)return null;
        int ne=(int)BitConverter.ToUInt32(idd,off);
        int ver=(int)BitConverter.ToUInt32(idd,off+4);
        if(ne>200||ver>10||ver==0)return null;
        int pos=off+16;
        var entries=new List<int[]>();
        for(int i=0;i<ne&&pos+8<=off+size;i++,pos+=8)
            entries.Add(new int[]{i,(int)BitConverter.ToUInt32(idd,pos),(int)BitConverter.ToUInt32(idd,pos+4)});
        var chs=new Dictionary<int,AnimChannel>();
        foreach(var e in entries)
        {
            int bi=e[0],ct=e[1],vc=e[2];
            if(ct==0||vc==0)continue;
            int chId=bi*4+(ct==0x17?0:ct==0x19?2:3);
            if(!chs.ContainsKey(chId))chs[chId]=new AnimChannel{Id=chId};
            var ch=chs[chId];
            for(int v=0;v<vc&&pos+1<off+size;v++,pos+=2)
            {short raw=(short)(idd[pos]|(idd[pos+1]<<8));if(raw!=-1)ch.Values.Add((float)raw);}
        }
        return chs.Count>0?chs:null;
    }

    // ════════════════════════════════════════════════════════
    //  JSON WRITER
    // ════════════════════════════════════════════════════════
    static void WriteAnimJson(AnimPair anim, string path)
    {
        using (var w=new StreamWriter(path,false,Encoding.UTF8))
        {
            w.WriteLine("{");
            w.WriteLine("  \"format\": \"EmoTE_v6\",");
            w.WriteLine("  \"pair_index\": "+anim.PairIndex+",");
            w.WriteLine("  \"subtag\": "+JsonStr(anim.SubTag)+",");
            w.WriteLine("  \"variant\": "+JsonStr(g_Variant.ToString())+",");
            if(!string.IsNullOrEmpty(anim.MapId))  w.WriteLine("  \"map_id\": "+JsonStr(anim.MapId)+",");
            if(!string.IsNullOrEmpty(anim.MapName))w.WriteLine("  \"map_name\": "+JsonStr(anim.MapName)+",");
            w.WriteLine("  \"ic_encoding\": \"Q15_int16_div32768\",");
            w.WriteLine("  \"st_format\": "+JsonStr(anim.StFormat)+",");
            w.WriteLine("  \"st_note\": "+JsonStr(anim.StFormat=="table"
                ?"raw int16: rotation=degrees, position=pixels, -1=no-keyframe"
                :"Q15 int16 bytecode")+",");
            w.WriteLine("  \"bone_model\": \"bone=ch/4, dof=['x','y','z','w'][ch%4]\",");
            if(g_Skel.Loaded){w.Write("  \"bone_names\": [");for(int i=0;i<g_Skel.BoneNames.Length;i++)w.Write((i>0?",":"")+JsonStr(g_Skel.BoneNames[i]));w.WriteLine("],");}
            w.WriteLine("  \"ic\": {"); WriteChannels(w,anim.IC); w.WriteLine("  },");
            w.WriteLine("  \"st\": {"); WriteChannels(w,anim.ST); w.WriteLine("  }");
            w.WriteLine("}");
        }
    }

    static void WriteChannels(StreamWriter w, Dictionary<int,AnimChannel> chs)
    {
        var keys=chs.Keys.OrderBy(k=>k).ToList();
        string[] dn={"x","y","z","w"};
        for(int ki=0;ki<keys.Count;ki++)
        {
            int id=keys[ki]; var ch=chs[id];
            int bone=id/4; string dof=dn[id%4];
            string boneN=g_Skel.Loaded&&bone<g_Skel.BoneNames.Length?g_Skel.BoneNames[bone]:"bone_"+bone;
            w.WriteLine("    "+JsonStr("ch"+id.ToString("D2"))+": {");
            w.WriteLine("      \"bone\": "+bone+",");
            w.WriteLine("      \"bone_name\": "+JsonStr(boneN)+",");
            w.WriteLine("      \"dof\": "+JsonStr(dof)+",");
            w.WriteLine("      \"count\": "+ch.Values.Count+",");
            w.Write    ("      \"values\": [");
            for(int vi=0;vi<ch.Values.Count;vi++){if(vi>0)w.Write(",");w.Write(ch.Values[vi].ToString("G6",System.Globalization.CultureInfo.InvariantCulture));}
            w.Write("]");
            if(ch.Tangents.Count>0){w.WriteLine(",");w.Write("      \"tangents\": [");for(int ti=0;ti<ch.Tangents.Count;ti++){if(ti>0)w.Write(",");w.Write(ch.Tangents[ti].ToString("G6",System.Globalization.CultureInfo.InvariantCulture));}w.Write("]");}
            w.WriteLine(); w.WriteLine("    "+(ki<keys.Count-1?"},":"}"));
        }
    }

    // ════════════════════════════════════════════════════════
    //  IDX LOADER
    // ════════════════════════════════════════════════════════
    static List<IdxRecord> LoadIdx(string path)
    {
        byte[] raw=File.ReadAllBytes(path);
        g_Variant=DetectVariant(raw);
        switch(g_Variant)
        {
            case IdxVariant.CharTE: return LoadIdxFixed(raw,47,55);
            case IdxVariant.EmoTE:  return LoadIdxFixed(raw,48,56);
            default:                return LoadIdxFxScan(raw);
        }
    }

    static IdxVariant DetectVariant(byte[] raw)
    {
        if(raw.Length>47+55){byte[] bvat={0x62,0x76,0x61,0x74};if(raw[47+24]==bvat[0]&&raw[47+25]==bvat[1]&&raw[47+26]==bvat[2]&&raw[47+27]==bvat[3])return IdxVariant.CharTE;}
        if(raw.Length>48+56){byte[] ghar={0x67,0x68,0x61,0x72};if(raw[48+24]==ghar[0]&&raw[48+25]==ghar[1]&&raw[48+26]==ghar[2]&&raw[48+27]==ghar[3])return IdxVariant.EmoTE;}
        return IdxVariant.Fx;
    }

    static List<IdxRecord> LoadIdxFixed(byte[] raw, int hdr, int rsz)
    {
        int n=(raw.Length-hdr)/rsz;
        var recs=new List<IdxRecord>(n);
        for(int i=0;i<n;i++)
        {
            int b=hdr+i*rsz; var r=new IdxRecord{Idx=i};
            r.TypeFlags=BitConverter.ToUInt32(raw,b);
            Array.Copy(raw,b+4,r.SubTag,0,4);
            r.IddOffset=BitConverter.ToUInt32(raw,b+8);
            r.Field2=BitConverter.ToUInt32(raw,b+12);
            r.Field3=BitConverter.ToUInt32(raw,b+16);
            r.KeySize=BitConverter.ToUInt32(raw,b+20);
            Array.Copy(raw,b+24,r.KeyTag,0,4);
            int kdl=rsz-28; r.KeyData=new byte[kdl]; Array.Copy(raw,b+28,r.KeyData,0,kdl);
            recs.Add(r);
        }
        return recs;
    }

    static List<IdxRecord> LoadIdxFxScan(byte[] raw)
    {
        var recs=new List<IdxRecord>(); int idx=0;
        for(int scan=0;scan<=raw.Length-24;scan+=4)
        {
            uint tf=BitConverter.ToUInt32(raw,scan);
            if(tf!=TF_ST&&tf!=TF_IC)continue;
            uint ioff=BitConverter.ToUInt32(raw,scan+8); if(ioff<100)continue;
            uint f2=BitConverter.ToUInt32(raw,scan+12); if(f2==0||f2>64*1024*1024)continue;
            uint ksz=BitConverter.ToUInt32(raw,scan+20); if(ksz==0||ksz>256)continue;
            var r=new IdxRecord{Idx=idx++};
            r.TypeFlags=tf; Array.Copy(raw,scan+4,r.SubTag,0,4);
            r.IddOffset=ioff; r.Field2=f2; r.Field3=BitConverter.ToUInt32(raw,scan+16); r.KeySize=ksz;
            int kdStart=scan+24; int kdLen=Math.Min((int)ksz,raw.Length-kdStart);
            r.KeyData=new byte[kdLen]; if(kdLen>0)Array.Copy(raw,kdStart,r.KeyData,0,kdLen);
            recs.Add(r);
        }
        return recs;
    }

    // ════════════════════════════════════════════════════════
    //  BLOCK SIZE + BOUNDARIES
    // ════════════════════════════════════════════════════════
    static SortedSet<int> BuildBoundaries(List<IdxRecord> recs, int iddLen)
    {
        var s=new SortedSet<int>();
        foreach(var r in recs)if(r.IddOffset>100&&r.IddOffset<(uint)iddLen)s.Add((int)r.IddOffset);
        s.Add(iddLen); return s;
    }

    static int GetBlockSize(SortedSet<int> bounds, IdxRecord r, int iddLen)
    {
        if(g_Variant==IdxVariant.CharTE||g_Variant==IdxVariant.Fx)
        {int sz=(int)r.Field2;return(sz>0&&(int)r.IddOffset+sz<=iddLen)?sz:0;}
        var view=bounds.GetViewBetween((int)r.IddOffset+1,int.MaxValue);
        return view.Count>0?view.Min-(int)r.IddOffset:0;
    }

    // ════════════════════════════════════════════════════════
    //  PAIR FINDER + PAIR INDEX
    // ════════════════════════════════════════════════════════
    static List<AnimPairRef> FindAnimPairs(List<IdxRecord> recs)
    {
        var pairs=new List<AnimPairRef>();
        for(int i=0;i<recs.Count-1;i++)
            if(recs[i].TypeFlags==TF_ST&&recs[i+1].TypeFlags==TF_IC)
                pairs.Add(new AnimPairRef{ST=recs[i],IC=recs[i+1]});
        return pairs;
    }

    static void WriteAnimPairIndex(List<IdxRecord> recs, string outDir)
    {
        var pairs=FindAnimPairs(recs);
        using(var sw=new StreamWriter(Path.Combine(outDir,"_anim_pairs.tsv"),false,Encoding.UTF8))
        {
            sw.WriteLine("PairIdx\tStIdx\tIcIdx\tSubTag\tStOffset\tIcOffset");
            foreach(var p in pairs)
                sw.WriteLine(p.ST.Idx+"\t"+p.ST.Idx+"\t"+p.IC.Idx+"\t"+p.ST.SubTagStr+"\t"+p.ST.IddOffset+"\t"+p.IC.IddOffset);
        }
    }

    // ════════════════════════════════════════════════════════
    //  OGG EXTRACTOR
    // ════════════════════════════════════════════════════════
    static int ExtractOgg(byte[] idd, string outDir)
    {
        var pos=new List<int>(); for(int p=0;p<=idd.Length-4;){int f=FindBytes(idd,MAGIC_OGG,p,idd.Length-p);if(f<0)break;pos.Add(f);p=f+4;}
        int saved=0;
        foreach(int start in pos)
        {
            if(start+27>idd.Length)continue;
            if(idd[start+4]!=0x00||idd[start+5]!=0x02)continue;
            byte[] serial=new byte[4]; Array.Copy(idd,start+14,serial,0,4);
            var ms=new MemoryStream(); bool eos=false;
            foreach(int pp in pos)
            {
                if(pp<start)continue; if(pp+27>idd.Length)break;
                byte[] ps=new byte[4]; Array.Copy(idd,pp+14,ps,0,4);
                if(!ps.SequenceEqual(serial)){if(pp>start+2*1024*1024)break;continue;}
                byte ht=idd[pp+5]; int ns=idd[pp+26]; if(pp+27+ns>idd.Length)break;
                int dl=0; for(int s=0;s<ns;s++)dl+=idd[pp+27+s];
                int pe=pp+27+ns+dl; if(pe>idd.Length)break;
                ms.Write(idd,pp,pe-pp); if((ht&0x04)!=0){eos=true;break;}
            }
            if(ms.Length<100){ms.Dispose();continue;}
            byte[] ogg=ms.ToArray(); ms.Dispose();
            string title=OggTitle(ogg)??"track_"+saved.ToString("D4");
            string fn=saved.ToString("D4")+"_"+SafeName(title,60)+".ogg";
            File.WriteAllBytes(Path.Combine(outDir,fn),ogg);
            Console.WriteLine("    ["+saved.ToString("D4")+"] "+fn+" ("+FmtSize(ogg.Length)+")"+(eos?"":" [no-EOS]"));
            saved++;
        }
        return saved;
    }

    static string OggTitle(byte[] ogg)
    {
        try{byte[] mk={0x03,0x76,0x6F,0x72,0x62,0x69,0x73};int p=FindBytes(ogg,mk,0,ogg.Length);if(p<0)return null;
        p+=7;int vl=BitConverter.ToInt32(ogg,p);p+=4+vl;int nc=BitConverter.ToInt32(ogg,p);p+=4;
        for(int i=0;i<nc&&p+4<=ogg.Length;i++){int cl=BitConverter.ToInt32(ogg,p);p+=4;if(p+cl>ogg.Length)break;
        string c=Encoding.UTF8.GetString(ogg,p,cl);p+=cl;if(c.StartsWith("TITLE=",StringComparison.OrdinalIgnoreCase))return c.Substring(6);}
        }catch{}return null;
    }

    // ════════════════════════════════════════════════════════
    //  SKELETON LOADER
    // ════════════════════════════════════════════════════════
    static void LoadSkeleton(string path)
    {
        if(!File.Exists(path)){Console.WriteLine("Skel not found: "+path);return;}
        Console.Write("Loading skeleton: "+path+" ... ");
        string ext=Path.GetExtension(path).ToLower();
        if(ext==".xml")LoadSkeletonXml(path);
        else if(ext==".json")LoadSkeletonJson(path);
        else if(ext==".skel")LoadSkeletonSpine(path);
        else{Console.WriteLine("unsupported '"+ext+"'");return;}
        if(g_Skel.Loaded)Console.WriteLine("OK ("+g_Skel.BoneNames.Length+" bones)");
        else Console.WriteLine("FAILED");
    }

    static void LoadSkeletonXml(string path)
    {
        string xml=File.ReadAllText(path,Encoding.UTF8);
        var names=new List<string>();var parents=new List<int>();
        var rx=new List<float>();var ry=new List<float>();var rr=new List<float>();var rs=new List<float>();
        var idx=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        int pos=0;
        while(pos<xml.Length){int ts=xml.IndexOf('<',pos);if(ts<0)break;int te=xml.IndexOf('>',ts);if(te<0)break;
        string tag=xml.Substring(ts+1,te-ts-1).Trim();pos=te+1;
        bool ib=tag.StartsWith("bone ",StringComparison.OrdinalIgnoreCase)||tag.StartsWith("bone\t",StringComparison.OrdinalIgnoreCase)||tag=="bone";
        if(!ib)continue;string nm=XmlAttr(tag,"name");if(string.IsNullOrEmpty(nm))continue;
        string pr=XmlAttr(tag,"parent");int id=names.Count;idx[nm]=id;names.Add(nm);
        int pid=-1;if(!string.IsNullOrEmpty(pr))idx.TryGetValue(pr,out pid);parents.Add(pid);
        rx.Add(ParseF(XmlAttr(tag,"x")));ry.Add(ParseF(XmlAttr(tag,"y")));
        rr.Add(ParseF(XmlAttr(tag,"rotation")));rs.Add(ParseF(XmlAttr(tag,"scaleX"),1f));}
        if(names.Count==0){pos=0;while(pos<xml.Length){int s=xml.IndexOf("<bone>",pos,StringComparison.OrdinalIgnoreCase);if(s<0)break;int e=xml.IndexOf("</bone>",s,StringComparison.OrdinalIgnoreCase);if(e<0)break;string n=xml.Substring(s+6,e-s-6).Trim();if(n.Length>0)names.Add(n);pos=e+7;}}
        if(names.Count==0)return;
        g_Skel=new SkeletonDef{Name=Path.GetFileNameWithoutExtension(path),BoneNames=names.ToArray(),
            Parents=parents.Count==names.Count?parents.ToArray():null,
            RestX=rx.Count==names.Count?rx.ToArray():null,RestY=ry.Count==names.Count?ry.ToArray():null,
            RestAngle=rr.Count==names.Count?rr.ToArray():null,RestScale=rs.Count==names.Count?rs.ToArray():null,Loaded=true};
    }

    static void LoadSkeletonJson(string path)
    {
        string json=File.ReadAllText(path,Encoding.UTF8).Trim();var names=new List<string>();
        if(json.StartsWith("["))foreach(string tok in json.Trim('[',']').Split(','))
            {string n=tok.Trim().Trim('"','\'');if(n.Length>0)names.Add(n);}
        else{int bs=json.IndexOf("\"bones\"");int p=bs>=0?bs:0;
        while(p<json.Length){int np=json.IndexOf("\"name\"",p);if(np<0)break;int co=json.IndexOf(':',np+6);if(co<0)break;
        int q1=json.IndexOf('"',co+1);if(q1<0)break;int q2=json.IndexOf('"',q1+1);if(q2<0)break;
        names.Add(json.Substring(q1+1,q2-q1-1));p=q2+1;}}
        if(names.Count==0)return;
        g_Skel=new SkeletonDef{Name=Path.GetFileNameWithoutExtension(path),BoneNames=names.ToArray(),Loaded=true};
    }

    static void LoadSkeletonSpine(string path)
    {
        byte[] data=File.ReadAllBytes(path);int p=0;
        try{string h=SpineStr(data,ref p);string ver=SpineStr(data,ref p);p+=8;
        int bc=SpineVInt(data,ref p);var names=new List<string>(bc);
        for(int i=0;i<bc&&p<data.Length;i++){string nm=SpineStr(data,ref p);if(i>0)SpineVInt(data,ref p);
        if(p+20<=data.Length)p+=20;if(p+4<=data.Length)p+=4;if(!string.IsNullOrEmpty(nm))names.Add(nm);}
        if(names.Count==0)return;Console.Write("Spine v"+ver+" "+bc+"b ... ");
        g_Skel=new SkeletonDef{Name=Path.GetFileNameWithoutExtension(path),BoneNames=names.ToArray(),Loaded=true};}
        catch(Exception ex){Console.WriteLine("\n  Error: "+ex.Message);}
    }

    static void PrintSkeleton()
    {
        if(!g_Skel.Loaded){Console.WriteLine("No skeleton.");return;}
        Console.WriteLine("\nSkeleton: "+g_Skel.Name+" — "+g_Skel.BoneNames.Length+" bones");
        for(int i=0;i<g_Skel.BoneNames.Length;i++){string p="";
        if(g_Skel.Parents!=null&&i<g_Skel.Parents.Length&&g_Skel.Parents[i]>=0)p=" < "+g_Skel.BoneNames[g_Skel.Parents[i]];
        Console.WriteLine(String.Format("  [{0:D2}] {1}{2}",i,g_Skel.BoneNames[i],p));}
        int mb=Math.Min(g_Skel.BoneNames.Length,8);
        Console.WriteLine("\nChannel mapping:");
        for(int b=0;b<mb;b++)Console.WriteLine(String.Format("  Bone {0} '{1}': CH{2:D2}=x CH{3:D2}=y CH{4:D2}=z CH{5:D2}=w",b,g_Skel.BoneNames[b],b*4,b*4+1,b*4+2,b*4+3));
    }

    // ════════════════════════════════════════════════════════
    //  SPINE + XML + UTILITY HELPERS
    // ════════════════════════════════════════════════════════
    static string SpineStr(byte[] d,ref int p){int l=SpineVInt(d,ref p);if(l<=0)return"";string s=Encoding.UTF8.GetString(d,p,l-1);p+=l-1;return s;}
    static int SpineVInt(byte[] d,ref int p){int r=0,sh=0;byte b;do{b=d[p++];r|=(b&0x7F)<<sh;sh+=7;}while((b&0x80)!=0&&p<d.Length);return r;}
    static string XmlAttr(string tag,string name){int ni=tag.IndexOf(name+"=",StringComparison.OrdinalIgnoreCase);if(ni<0)return"";int qi=ni+name.Length+1;if(qi>=tag.Length)return"";char q=tag[qi];if(q!='"'&&q!='\'')return"";int qe=tag.IndexOf(q,qi+1);return qe<0?"":tag.Substring(qi+1,qe-qi-1);}
    static float ReadQ15(byte[] d, int i)
    { return (float)(short)(d[i] | (d[i+1] << 8)) / 32768f; }

    static int FindBytes(byte[] hay, byte[] needle, int start, int len)
    {
        int lim = start + len - needle.Length;
        for (int i = start; i <= lim; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
                if (hay[i+j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    static int FindByte(byte[] d, byte val, int start, int len)
    { for (int i = start; i < start+len && i < d.Length; i++) if (d[i] == val) return i; return -1; }

    static bool BytesStartWith(byte[] d, int off, byte[] m)
    { if (off+m.Length > d.Length) return false; for (int i=0;i<m.Length;i++) if (d[off+i]!=m[i]) return false; return true; }

    static bool BytesContain(byte[] d, byte[] n, int max)
    { return FindBytes(d, n, 0, Math.Min(max, d.Length)) >= 0; }

    static bool IsAsciiPrintable(byte[] d, int off, int len)
    { for (int i=0;i<len;i++) if (d[off+i]<0x20||d[off+i]>0x7E) return false; return true; }

    static string DetectMagic(byte[] d)
    {
        if (d.Length < 4) return "bin";
        if (BytesStartWith(d,0,new byte[]{0x4F,0x67,0x67,0x53})) return "ogg";
        if (BytesStartWith(d,0,new byte[]{0x89,0x50,0x4E,0x47})) return "png";
        if (BytesStartWith(d,0,new byte[]{0xFF,0xD8,0xFF}))       return "jpeg";
        if (BytesStartWith(d,0,new byte[]{0x42,0x4D}))            return "bmp";
        if (BytesStartWith(d,0,new byte[]{0x1F,0x8B}))            return "gz";
        if (BytesStartWith(d,0,new byte[]{0x50,0x4B,0x03,0x04}))  return "zip";
        if (BytesContain(d,MAGIC_EMOTE,64))                       return "etbl";
        return "bin";
    }

    static string ExtFor(string t)
    {
        switch (t)
        {
            case "ogg":  return ".ogg";
            case "png":  return ".png";
            case "jpeg": return ".jpg";
            case "bmp":  return ".bmp";
            case "gz":   return ".gz";
            case "zip":  return ".zip";
            case "etbl": return ".etbl";
            default:     return ".bin";
        }
    }

    static string ToHex(byte[] b)
    { return BitConverter.ToString(b).Replace("-","").ToLower(); }

    static string SafeName(string s, int max)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
            sb.Append((char.IsLetterOrDigit(c)||c=='-'||c=='_'||c==' ') ? c : '_');
        string r = sb.ToString().Trim('_',' ');
        if (r.Length == 0) r = "unk";
        r = r.Replace(' ','_');
        return r.Length <= max ? r : r.Substring(0, max);
    }

    static string JsonStr(string s)
    { return "\"" + ((s ?? "").Replace("\\","\\\\").Replace("\"","\\\"")) + "\""; }

    static string FmtSize(long n)
    { return n.ToString("N0") + " bytes"; }

    static string MkDir(string p, string s)
    { string d = Path.Combine(p,s); Directory.CreateDirectory(d); return d; }

    static float ParseF(string s, float def)
    { float v; return float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : def; }

    static float ParseF(string s)
    { return ParseF(s, 0f); }

    static void Banner(string s)
    { Console.WriteLine(new string('=',58)); Console.WriteLine("  "+s); Console.WriteLine(new string('=',58)); }

    static void Need(string[] a, int n, string u)
    { if (a.Length < n) Die("Usage: " + u); }

    static void Die(string m)
    { Console.Error.WriteLine("[ERROR] " + m); Environment.Exit(1); }
}
