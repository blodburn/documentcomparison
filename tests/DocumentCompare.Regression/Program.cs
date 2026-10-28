using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using DocumentCompare.Avalonia.Engine;
using DocumentCompare.Avalonia.Models;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

// Developer-only cross-language parity probe. It is not part of the desktop application.
if (args.Length==2 && args[0]=="--code-diff-corpus")
{
    using var input=System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[1]));
    var results=new List<object>();
    foreach(var corpusCase in input.RootElement.EnumerateArray())
    {
        var before=corpusCase.GetProperty("before").GetString() ?? "";
        var after=corpusCase.GetProperty("after").GetString() ?? "";
        var compared=NativeCodeVersionComparer.Compare(before,after,out var usedNative);
        if (Environment.GetEnvironmentVariable("DOCUMENTCOMPARE_REQUIRE_NATIVE")=="1" && !usedNative)
            throw new InvalidDataException("Rust native code diff must be loaded for parity corpus.");
        results.Add(compared.Changes.Select(change=>new
        {
            kind=change.Kind switch {"추가"=>"Insert","삭제"=>"Delete",_=>"Replace"},
            beforeStart=change.BeforeStart, beforeEnd=change.BeforeEnd,
            afterStart=change.AfterStart, afterEnd=change.AfterEnd
        }).ToArray());
    }
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(results));
    return;
}

// Developer-only fixture generator. Its checked-in output is compared against
// BOTH the canonical C# parser and the Rust native parser in normal regressions.
if (args.Length==2 && args[0]=="--document-parts-golden")
{
    var method=typeof(NativeComparisonEngine).GetMethod(
        "ParsePartsCSharp",BindingFlags.Static|BindingFlags.NonPublic)!;
    var inputs=System.Text.Json.JsonSerializer.Deserialize<List<string>>(
        File.ReadAllText(args[1]))!;
    var records=new List<object>();
    foreach(var input in inputs)
    {
        var parts=(System.Collections.IEnumerable)method.Invoke(null,new object[]{input})!;
        var expected=parts.Cast<object>().Select(obj=>{
            var type=obj.GetType();
            object Get(string prop)=>type.GetProperty(prop)!.GetValue(obj)!;
            return new {
                label=Get("Label"),start=Get("Start"),end=Get("End"),
                coreStart=Get("CoreStart"),coreEnd=Get("CoreEnd"),core=Get("Core")
            };
        }).ToArray();
        records.Add(new {input,parts=expected});
    }
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(records,
        new System.Text.Json.JsonSerializerOptions {WriteIndented=true}));
    return;
}

const string W="http://schemas.openxmlformats.org/wordprocessingml/2006/main";
var dir=Path.Combine(Path.GetTempPath(), "DocumentCompareRegression", Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir); XNamespace w=W;
static void Check(bool ok,string msg){if(!ok)throw new Exception(msg);}
static void Put(ZipArchive z,string p,string c){var e=z.CreateEntry(p);using var sw=new StreamWriter(e.Open(),new UTF8Encoding(false));sw.Write(c);}
static void Make(string path,string body){using var fs=new FileStream(path,FileMode.Create);using var z=new ZipArchive(fs,ZipArchiveMode.Create);Put(z,"[Content_Types].xml","<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");Put(z,"_rels/.rels","<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");Put(z,"word/document.xml","<?xml version=\"1.0\"?><w:document xmlns:w=\""+W+"\"><w:body>"+body+"<w:sectPr/></w:body></w:document>");}
static string P(string t)=>"<w:p><w:r><w:t xml:space=\"preserve\">"+System.Security.SecurityElement.Escape(t)+"</w:t></w:r></w:p>";
static XDocument Doc(string path){using var z=ZipFile.OpenRead(path);using var st=z.GetEntry("word/document.xml")!.Open();return XDocument.Load(st);}
static async Task<string> Read(string path){var t=typeof(NativeComparisonEngine).Assembly.GetType("DocumentCompare.Avalonia.Engine.NativeDocumentReader")!;var m=t.GetMethod("ReadAsync",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!;return await (Task<string>)m.Invoke(null,new object[]{path,CancellationToken.None})!;}
var eng=new NativeComparisonEngine();

// Production-path smoke test for the Rust-first whole matcher and the
// Rust-first sentence review. This intentionally does not enable the
// shadow C# verification environment used in the full regression suite.
if (args.Length==1 && args[0]=="--smoke-native-production")
{
    Check(NativeCodeVersionComparer.IsAvailable,
        "Native Rust library was not embedded or found in production mode");
    static long GetRustCounter(string property) =>
        (long)typeof(NativeComparisonEngine).GetProperty(property,
            BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
    var oldDoc=Path.Combine(dir,"production-rust-26.docx");
    var newDoc=Path.Combine(dir,"production-rust-27.docx");
    var commonA="회원은 제공된 서비스의 이용요금과 관련 세금의 지급 조건에 동의하며 계약에 따른 의무를 이행합니다.";
    var oldOnly="회원은 언제든지 서비스를 통하여 제공되는 상품과 용역의 가격을 수정할 수 있습니다.";
    var commonB="회원은 서비스 종료 후에도 기존 계약에 명시된 개인정보 처리 및 반환 절차를 준수하여야 합니다.";
    var newOnly="다만, 관련 법령 및 이 약관에 따른 청약철회·환급 등 회원의 권리는 제한되지 않습니다.";
    Make(oldDoc,P("제26조(수수료의 지급)")+P(commonA+" "+oldOnly+" "+commonB));
    Make(newDoc,P("제27조(수수료의 지급)")+P(commonA+" "+commonB+" "+newOnly));
    var initialRaw=GetRustCounter("RawPairRustMatches");
    var compared=await eng.CompareAsync(new[]{oldDoc,newDoc},0,"legal",true,true);
    var article=compared.Rows.Single(x=>x.Members[0]?.Number=="26");
    Check(article.Markers.Count(m=>m.Part=="header"&&m.Message.Contains("27"))==1 &&
          article.Markers.Count(m=>m.Part=="body"&&m.Action=="삭제"&&m.Text==oldOnly)==1 &&
          article.Markers.Count(m=>m.Part=="body"&&m.Action=="추가"&&m.Text==newOnly)==1 &&
          article.Markers.Where(m=>m.Part=="body")
              .All(m=>!m.Text.Contains(commonA)&&!m.Text.Contains(commonB)),
        "production Rust-first legal article markers differ from exact expected result");
    Check(GetRustCounter("RawPairRustMatches")>initialRaw &&
          GetRustCounter("RawPairRustUnsupported")==0,
        "production raw-input Rust ComparePair did not own article and sentence review");
    var generalBefore=Path.Combine(dir,"rust-general-old.docx");
    var generalAfter=Path.Combine(dir,"rust-general-new.docx");
    Make(generalBefore,P("Basic introductory paragraph")+P("General privacy terms"));
    Make(generalAfter,P("General privacy terms")+P("Basic introductory paragraph"));
    var beforeGeneric=GetRustCounter("GenericUnitRustMatches");
    var genericCmp=await eng.CompareAsync(new[]{generalBefore,generalAfter},0,"general",true,true);
    Check(genericCmp.Rows.Count>=2 &&
          GetRustCounter("GenericUnitRustMatches")>beforeGeneric,
        "production general-document matcher must use Rust-first execution");
    var lineageBefore=Path.Combine(dir,"rust-lineage-before.docx");
    var lineageAfter=Path.Combine(dir,"rust-lineage-after.docx");
    Make(lineageBefore,P("제1조(목적)")+P("본 약관은 이용 목적과 관련 의무를 규정한다.")+
        P("제2조(보호)")+P("회사는 회원의 개인정보를 보호할 의무가 있다."));
    Make(lineageAfter,P("제1조(보호)")+P("회사는 회원의 개인정보를 보호할 의무가 있다.")+
        P("제2조(목적)")+P("본 약관은 이용 목적과 관련 의무를 규정한다."));
    var initialLineage=GetRustCounter("UnitLineageRustMatches");
    var lineageResult=await eng.CompareAsync(new[]{lineageBefore,lineageAfter},
        0,"legal",true,true);
    Check(lineageResult.Rows.Count>=2 &&
        GetRustCounter("UnitLineageRustMatches")>initialLineage,
        "production article movement/renumbering must execute the Rust lineage engine");
    var tableBefore=Path.Combine(dir,"rust-table-before.docx");
    var tableAfter=Path.Combine(dir,"rust-table-after.docx");
    static string TableRow(string label,string value) =>
        "<w:tr><w:tc>"+P(label)+"</w:tc><w:tc>"+P(value)+"</w:tc></w:tr>";
    static string TableXml(string rows) =>
        "<w:tbl><w:tblPr/><w:tblGrid><w:gridCol/><w:gridCol/></w:tblGrid>"+
        rows+"</w:tbl>";
    Make(tableBefore,TableXml(TableRow("ID1","A")+TableRow("ID2","B")));
    Make(tableAfter,TableXml(TableRow("ID2","B")+TableRow("ID1","A")));
    var initialTable=GetRustCounter("TableCellRustMatches");
    var tableResult=await eng.CompareAsync(new[]{tableBefore,tableAfter},
        0,"general",true,true);
    Check(tableResult.Rows.Count==4 &&
        GetRustCounter("TableCellRustMatches")>initialTable,
        "production moved DOCX table rows must execute Rust cell correspondence");
    var wordBefore=Path.Combine(dir,"rust-token-before.docx");
    var wordAfter=Path.Combine(dir,"rust-token-after.docx");
    Make(wordBefore,P("Members shall provide proof of identity and contact information."));
    Make(wordAfter,P("Members shall provide updated identity documents and emergency contact information."));
    var initialFullReview=GetRustCounter("RawPairRustMatches");
    var wordReview=await eng.CompareAsync(new[]{wordBefore,wordAfter},
        0,"general",true,true);
    Check(wordReview.Rows.Any(x=>x.Changed) &&
        GetRustCounter("RawPairRustMatches")>initialFullReview,
        "production general word edits must use source-owned Rust ComparePair");
    Check(GetRustCounter("RawPairRustMatches")>0 &&
        GetRustCounter("GenericUnitRustMatches")>0,
        "production raw Rust document review and generic scoring were not used");
    var beforeMorph=GetRustCounter("KoreanReviewRustMatches");
    var morph=typeof(NativeComparisonEngine).GetMethod("TryKoreanReviewDiff",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var koreanMarkers=(System.Collections.IEnumerable?)morph.Invoke(null,
        new object[]{"개인정보 동의서 제출기한 요구",
            "개인정보 동의서는 제출기한을 요구",
            0,0,0,1,"A↔B",0,"body",0});
    Check(koreanMarkers is not null &&
        koreanMarkers.Cast<object>().Count()>=2 &&
        GetRustCounter("KoreanReviewRustMatches")>beforeMorph,
        "production Rust-first Korean morphology alignment not adopted");
    Check(GetRustCounter("ReviewSegmentsRustMatches")>0,
        "production Word review text segmentation must run in Rust");
    Check(GetRustCounter("RowAlignmentRustMatches")>0,
        "production A/B/C row positioning must run in Rust");
    Check(GetRustCounter("UnitParserRustMatches")>0,
        "production legal/general unit parsing must run in Rust");
    Check(GetRustCounter("NormalizationRustMatches")>0,
        "production NFC/NFKC matching must run in Rust");
    // Direct source-only ComparePair invokes its Rust review and coalescing
    // algorithms internally, so the old per-FFI facade counters no longer
    // represent actual native ownership in the production path.
    Check(GetRustCounter("RawPairRustMatches")>0 &&
        GetRustCounter("RawPairRustUnsupported")==0,
        "production article/part comparison must be Rust-only without C# fallback");
    Console.WriteLine("PASS PRODUCTION RUST-FIRST ITEM, LINEAGE, TABLE, TOKEN, SCORE, KOREAN DP, SENTENCE AND REVIEW SEGMENTS");
    return;
}

// Retirement gate: the production document pair decision is exclusively
// Rust-owned. Reintroducing any managed ComparePair fallback is a regression.
Check(typeof(NativeComparisonEngine).GetMethod("ComparePairCore",
        BindingFlags.NonPublic|BindingFlags.Static) is null &&
      typeof(NativeComparisonEngine).GetMethod("BuildGlobalPartIndex",
        BindingFlags.NonPublic|BindingFlags.Static) is null &&
      typeof(NativeCodeVersionComparer).GetMethod("PlanDocumentPair",
        BindingFlags.Public|BindingFlags.Static) is null,
    "C# legacy ComparePair/feature-planner dependency was reintroduced");
Console.WriteLine("PASS RUST-ONLY COMPAREPAIR RETIREMENT (no managed core/index/feature-plan)");

// 1. Hidden runs: screen and Word export must use the same visible-text policy.
var ha=Path.Combine(dir,"hiddenA.docx");var hb=Path.Combine(dir,"hiddenB.docx");var ho=Path.Combine(dir,"hiddenOut.docx");
Make(ha,P("Alpha Beta"));Make(hb,"<w:p><w:r><w:t xml:space=\"preserve\">Alpha </w:t></w:r><w:r><w:rPr><w:vanish/></w:rPr><w:t xml:space=\"preserve\">HIDDEN </w:t></w:r><w:r><w:t>Beta</w:t></w:r></w:p>");
var hc=await eng.CompareAsync(new[]{ha,hb},0,"general",true,true);Check(!hc.Rows.Any(r=>r.Changed),"hidden run changed UI");await eng.ExportWordAsync(ha,hb,ho,"T",true,default,hc,0,1);var hd=Doc(ho);Check(!hd.Descendants().Any(x=>x.Name==w+"ins"||x.Name==w+"del"),"hidden run created Word revisions");Console.WriteLine("PASS HIDDEN RUN CONSISTENCY");

string Row(string a,string b)=>"<w:tr><w:tc>"+P(a)+"</w:tc><w:tc>"+P(b)+"</w:tc></w:tr>";
string Tbl(string rows)=>"<w:tbl><w:tblPr/><w:tblGrid><w:gridCol/><w:gridCol/></w:tblGrid>"+rows+"</w:tbl>";

// Real DOCX fixtures for the A/B/C comparison path (distinct from the Word exporter tests).
{
    string Cell(string xml)=>"<w:tc>"+xml+"</w:tc>";
    string Cells(params string[] texts)=>"<w:tr>"+string.Concat(texts.Select(x=>Cell(P(x))))+"</w:tr>";
    string Table(params string[] rows)=>Tbl(string.Concat(rows));
    async Task<ComparisonResultVm> CompareTable(string name,string oldXml,string newXml)
    {
        var pathA=Path.Combine(dir,"struct-"+name+"-A.docx");
        var pathB=Path.Combine(dir,"struct-"+name+"-B.docx");
        Make(pathA,oldXml);Make(pathB,newXml);
        return await eng.CompareAsync(new[]{pathA,pathB},0,"general",false,true);
    }
    static List<ComparisonRowVm> CellsOnly(ComparisonResultVm r)=>
        r.Rows.Where(x=>x.Members.Any(m=>m?.Kind=="table-cell")).ToList();
    var same=await CompareTable("same",P("Before")+Table(Cells("A1","B1"),Cells("A2","B2"))+P("After"),
        P("Before")+Table(Cells("A1","B1"),Cells("A2","B2"))+P("After"));
    Check(CellsOnly(same).Count==4&&CellsOnly(same).All(x=>!x.Changed),"unchanged table must preserve 4 unchanged cells");
    Check(same.Rows.First().Members[0]!.Text=="Before"&&same.Rows.Last().Members[0]!.Text=="After",
        "table placement displaced surrounding paragraphs");
    Console.WriteLine("PASS STRUCTURED TABLE UNCHANGED AND PARAGRAPH ORDER");

    var edit=await CompareTable("cell-edit",Table(Cells("Account","Amount"),Cells("Rent","100")),
        Table(Cells("Account","Amount"),Cells("Rent","150")));
    var edited=CellsOnly(edit).Where(x=>x.Changed).ToList();
    Check(edited.Count==1&&edited[0].Members[0]!.Body=="100"&&edited[0].Members[1]!.Body=="150",
        "single cell partial edit did not stay in the corresponding cell");
    Check(edited[0].Markers.Any(m=>m.Part=="body"),"edited cell has no body change marker");
    Console.WriteLine("PASS STRUCTURED CELL CONTENT DIFF");

    var para=await CompareTable("cell-paragraph",Table("<w:tr>"+Cell(P("First"))+"</w:tr>"),
        Table("<w:tr>"+Cell(P("First")+P("Second"))+"</w:tr>"));
    Check(CellsOnly(para).Count==1&&CellsOnly(para)[0].Changed&&
          CellsOnly(para)[0].Members[1]!.Body=="First\nSecond"&&
          CellsOnly(para)[0].DisplayMessages.Any(m=>m.Contains("문단")),
          "multiple paragraphs within one cell not reported");
    Console.WriteLine("PASS STRUCTURED CELL PARAGRAPH INSERTION");

    var addedRow=await CompareTable("row-add",Table(Cells("ID1","Stable")),
        Table(Cells("ID1","Stable"),Cells("ID2","New")));
    Check(CellsOnly(addedRow).Count==4&&CellsOnly(addedRow).Count(x=>x.Changed)==2 &&
          CellsOnly(addedRow).Count(x=>x.Members[0] is null)==2,
          "inserted row must be two new cells, without modifying the surviving row");
    var deletedRow=await CompareTable("row-delete",Table(Cells("ID1","Stable"),Cells("ID2","Old")),
        Table(Cells("ID1","Stable")));
    Check(CellsOnly(deletedRow).Count==4&&CellsOnly(deletedRow).Count(x=>x.Members[1] is null)==2,
        "deleted row must not be merged into surviving cells");
    Console.WriteLine("PASS STRUCTURED ROW INSERTION AND DELETION");

    var movedRow=await CompareTable("row-move",Table(Cells("ID1","Alpha"),Cells("ID2","Beta")),
        Table(Cells("ID2","Beta"),Cells("ID1","Alpha")));
    Check(CellsOnly(movedRow).Count==4&&CellsOnly(movedRow).All(x=>x.Members.All(m=>m is not null)),
        "moved rows were mistaken for added/deleted rows");
    Check(CellsOnly(movedRow).Any(x=>x.DisplayMessages.Any(m=>m.Contains("표 행 이동"))),
        "reordered table rows lacked a structural move message");
    Console.WriteLine("PASS STRUCTURED ROW REORDER MATCHING");

    var addedCol=await CompareTable("col-add",Table(Cells("LEFT","RIGHT")),
        Table(Cells("LEFT","MIDDLE","RIGHT")));
    Check(CellsOnly(addedCol).Count==3&&CellsOnly(addedCol).Count(x=>x.Members[0] is null)==1&&
          CellsOnly(addedCol).Single(x=>x.Members[0] is null).Members[1]!.Body=="MIDDLE",
          "column insertion was misaligned against neighboring cells");
    var deletedCol=await CompareTable("col-delete",Table(Cells("LEFT","MIDDLE","RIGHT")),
        Table(Cells("LEFT","RIGHT")));
    Check(CellsOnly(deletedCol).Count==3&&CellsOnly(deletedCol).Count(x=>x.Members[1] is null)==1,
        "column deletion was misaligned against neighboring cells");
    Console.WriteLine("PASS STRUCTURED COLUMN INSERTION AND DELETION");

    var tableShift=await CompareTable("table-shift",Table(Cells("DELETE","ME"))+
        P("Between")+Table(Cells("KEEP","ME")),P("Between")+Table(Cells("KEEP","ME")));
    Check(CellsOnly(tableShift).Count==4&&CellsOnly(tableShift).Count(x=>x.Members[1] is null)==2&&
          CellsOnly(tableShift).Count(x=>x.Members[0] is not null&&x.Members[1] is not null)==2,
          "deleted table incorrectly matched to the next table");
    Console.WriteLine("PASS STRUCTURED WHOLE TABLE DELETION AND REINDEXING");

    var mergeOld=Table("<w:tr><w:tc><w:tcPr><w:gridSpan w:val=\"2\"/></w:tcPr>"+P("Merged")+
        "</w:tc></w:tr>");
    var mergeNew=Table("<w:tr><w:tc><w:tcPr><w:gridSpan w:val=\"3\"/></w:tcPr>"+P("Merged")+
        "</w:tc></w:tr>");
    var merge=await CompareTable("merge-span",mergeOld,mergeNew);
    Check(CellsOnly(merge).Count==1&&CellsOnly(merge)[0].Changed&&
          CellsOnly(merge)[0].DisplayMessages.Any(x=>x.Contains("병합")),
          "gridSpan structural change invisible");
    Console.WriteLine("PASS STRUCTURED GRIDSPAN CHANGE");

    var nested="<w:tbl><w:tr><w:tc>"+P("Outer")+
        "<w:sdt><w:sdtContent><w:tbl><w:tr><w:tc>"+P("Inner")+
        "</w:tc></w:tr></w:tbl></w:sdtContent></w:sdt>"+P("Tail")+
        "</w:tc></w:tr></w:tbl>";
    var nestedCompare=await CompareTable("nested",nested,nested);
    Check(CellsOnly(nestedCompare).Count==2&&CellsOnly(nestedCompare).All(x=>!x.Changed)&&
          CellsOnly(nestedCompare).Any(x=>x.Members[0]!.Body.Contains("Inner"))&&
          CellsOnly(nestedCompare).Any(x=>x.Members[0]!.Body.Contains("Outer\nTail")),
          "nested SDT table duplicated/lost a cell");
    Console.WriteLine("PASS STRUCTURED NESTED SDT TABLE");

    var repeated=await CompareTable("duplicate",Table(Cells("Repeated","Same"),Cells("Repeated","Same")),
        Table(Cells("Repeated","Same"),Cells("Repeated","Same")));
    Check(CellsOnly(repeated).Count==4&&CellsOnly(repeated).All(x=>!x.Changed),
        "repeated rows must not collapse into one cell");
    Console.WriteLine("PASS STRUCTURED DUPLICATE CELLS");

    var vMergeOld=Table("<w:tr><w:tc><w:tcPr><w:vMerge w:val=\"restart\"/></w:tcPr>"+
        P("A")+"</w:tc></w:tr>");
    var vMergeNew=Table("<w:tr><w:tc><w:tcPr><w:vMerge/></w:tcPr>"+
        P("A")+"</w:tc></w:tr>");
    var vMergeResult=await CompareTable("vertical-merge",vMergeOld,vMergeNew);
    Check(CellsOnly(vMergeResult).Count==1&&CellsOnly(vMergeResult)[0].Changed&&
        CellsOnly(vMergeResult)[0].DisplayMessages.Any(m=>m.Contains("병합")),
        "vMerge restart/continue structural change not detected");
    Console.WriteLine("PASS STRUCTURED VERTICAL MERGE CHANGE");

    var irregular=await CompareTable("irregular",Table(Cells("X","Y")+Cells("Q")),
        Table(Cells("X","Y")+Cells("Q","NEW")));
    Check(CellsOnly(irregular).Count==4&&CellsOnly(irregular).Count(x=>x.Members[0] is null)==1,
        "nonrectangular table incorrectly fabricated/shifted cells");
    Console.WriteLine("PASS STRUCTURED NONRECTANGULAR TABLE");

    var tableAdded=await CompareTable("table-added",P("Before")+Table(Cells("KEEP","ONE"))+P("After"),
        P("Before")+Table(Cells("NEW","TWO"))+Table(Cells("KEEP","ONE"))+P("After"));
    Check(CellsOnly(tableAdded).Count==4&&CellsOnly(tableAdded).Count(x=>x.Members[0] is null)==2&&
        CellsOnly(tableAdded).Count(x=>x.Members.All(m=>m is not null))==2,
        "whole-table insertion consumed a surviving table");
    Console.WriteLine("PASS STRUCTURED WHOLE TABLE INSERTION");

    var tableMoved=await CompareTable("table-moved",Table(Cells("ONE","X"))+P("Middle")+
        Table(Cells("TWO","Y")),Table(Cells("TWO","Y"))+P("Middle")+Table(Cells("ONE","X")));
    Check(CellsOnly(tableMoved).Count==4&&CellsOnly(tableMoved).All(x=>x.Members.All(m=>m is not null)),
        "whole-table movement became deletion/insertion");
    Check(CellsOnly(tableMoved).Any(x=>x.DisplayMessages.Any(m=>m.Contains("표 이동"))),
        "whole-table reorder lacked a structural move message");
    Console.WriteLine("PASS STRUCTURED WHOLE TABLE REORDER MATCHING");

    var paraDeleted=await CompareTable("para-delete",Table("<w:tr>"+Cell(P("First")+P("Second"))+"</w:tr>"),
        Table("<w:tr>"+Cell(P("First"))+"</w:tr>"));
    Check(CellsOnly(paraDeleted).Count==1&&CellsOnly(paraDeleted)[0].Changed&&
        CellsOnly(paraDeleted)[0].DisplayMessages.Any(x=>x.Contains("문단")),
        "deleted paragraph inside a cell not flagged");
    Console.WriteLine("PASS STRUCTURED CELL PARAGRAPH DELETION");

    var cPath=Path.Combine(dir,"table-third.docx");
    Make(cPath,Table(Cells("Label","Version C")));
    var aPath=Path.Combine(dir,"table-A-3way.docx");
    var bPath=Path.Combine(dir,"table-B-3way.docx");
    Make(aPath,Table(Cells("Label","Version A")));Make(bPath,Table(Cells("Label","Version B")));
    var threeWay=await eng.CompareAsync(new[]{aPath,bPath,cPath},0,"general",true,true);
    Check(CellsOnly(threeWay).Count==2&&CellsOnly(threeWay).Any(r=>
        r.Members.Count==3&&r.Members.All(m=>m is not null)&&
        r.Markers.Any(m=>m.Pair=="A↔B")&&r.Markers.Any(m=>m.Pair=="B↔C")),
        "three-document structured cell changes failed to align all A/B/C versions");
    Console.WriteLine("PASS STRUCTURED A/B/C THREE-WAY CHANGES");

    var progressSuccess=new ComparisonProgressSession();
    Check(progressSuccess.TryAccept(0)&&progressSuccess.TryAccept(100),
        "progress callbacks must be accepted while comparing");
    progressSuccess.Close();
    Check(progressSuccess.IsClosed&&!progressSuccess.TryAccept(100),
        "late 100% callback overwrote completed state");
    var progressFailed=new ComparisonProgressSession();progressFailed.Close();
    Check(!progressFailed.TryAccept(100),"late callback overwrote failed state");
    var progressCanceled=new ComparisonProgressSession();progressCanceled.Close();
    Check(!progressCanceled.TryAccept(99),"late callback overwrote canceled state");
    var progressAgain=new ComparisonProgressSession();
    Check(progressAgain.TryAccept(0)&&!progressSuccess.TryAccept(0),
        "new run inherited stale previous-run progress state");
    Console.WriteLine("PASS PROGRESS TERMINAL SUCCESS/FAIL/CANCEL/RETRY");
}
if (args.Contains("--tables-only", StringComparer.Ordinal)) return;
// 2. Deleted table row must stay a deleted row, never get injected into a surviving cell.
var ta=Path.Combine(dir,"tableA.docx");var tb=Path.Combine(dir,"tableB.docx");var to=Path.Combine(dir,"tableOut.docx");
Make(ta,Tbl(Row("A1","B1")+Row("A2_DELETED","B2_DELETED")));Make(tb,Tbl(Row("A1","B1")));await eng.ExportWordAsync(ta,tb,to,"T",true);var td=Doc(to);var trs=td.Descendants(w+"tr").ToList();Check(trs.Count==2,"deleted table row not reconstructed");Check(trs[1].Element(w+"trPr")?.Element(w+"del") is not null,"deleted row lacks trPr/del");Check(!string.Concat(trs[0].Descendants(w+"t").Select(x=>x.Value)).Contains("A2_DELETED"),"deleted row leaked into surviving row");Check(string.Concat(trs[1].Descendants(w+"t").Select(x=>x.Value)).Contains("A2_DELETED"),"deleted row text missing");Console.WriteLine("PASS TABLE ROW DELETION");

// 3. Entirely new B row must be row-level tracked insertion.
var tia=Path.Combine(dir,"tableInsA.docx");var tib=Path.Combine(dir,"tableInsB.docx");var tio=Path.Combine(dir,"tableInsOut.docx");Make(tia,Tbl(Row("A1","B1")));Make(tib,Tbl(Row("A1","B1")+Row("A2_NEW","B2_NEW")));await eng.ExportWordAsync(tia,tib,tio,"T",true);var tid=Doc(tio);var itrs=tid.Descendants(w+"tr").ToList();Check(itrs.Count==2 && itrs[1].Element(w+"trPr")?.Element(w+"ins") is not null,"inserted row lacks trPr/ins");Console.WriteLine("PASS TABLE ROW INSERTION");

// 4. Textbox text exactly once, not lost or duplicated.
var da=Path.Combine(dir,"draw.docx");Make(da,"<w:p><w:r><w:t>Alpha</w:t><w:drawing><w:txbxContent><w:p><w:r><w:t>BOX</w:t></w:r></w:p></w:txbxContent></w:drawing></w:r><w:r><w:t xml:space=\"preserve\"> Beta</w:t></w:r></w:p>");var dr=await Read(da);Check(dr.Count(c=>c=='B')>=1 && dr.Split("BOX").Length-1==1,"textbox text lost/duplicated: "+dr);Check(dr.Contains("Alpha Beta")&&dr.Contains("BOX"),"textbox/direct text wrong: "+dr);Console.WriteLine("PASS TEXTBOX SINGLE EXTRACTION");

// 5. Existing revisions in B are rejected instead of silently mixing histories.
var ea=Path.Combine(dir,"existingA.docx");var eb=Path.Combine(dir,"existingB.docx");var eo=Path.Combine(dir,"existingOut.docx");Make(ea,P("Alpha"));Make(eb,"<w:p><w:r><w:t>Alpha</w:t></w:r><w:ins w:id=\"7\" w:author=\"Old\"><w:r><w:t xml:space=\"preserve\"> NEW</w:t></w:r></w:ins></w:p>");var threw=false;try{await eng.ExportWordAsync(ea,eb,eo,"Current",true);}catch(InvalidOperationException ex){threw=ex.Message.Contains("기존 Word 변경추적");}Check(threw,"existing revisions were silently mixed");Console.WriteLine("PASS EXISTING REVISION GUARD");

// 6. BOMless UTF-16LE.
var u16=Path.Combine(dir,"utf16.txt");File.WriteAllBytes(u16,new UnicodeEncoding(false,false).GetBytes("한글 ABC"));var us=await Read(u16);Check(us=="한글 ABC"&&!us.Contains('\0'),"BOMless UTF-16 misdecoded: "+us);Console.WriteLine("PASS BOMLESS UTF16");

// 7. Parenthesized alpha / roman enumerators.
var pm=typeof(NativeComparisonEngine).GetMethod("ParseParts",BindingFlags.Static|BindingFlags.NonPublic)!;var parsed=(System.Collections.IEnumerable)pm.Invoke(null,new object[]{"(a) Alpha\n(b) Beta\n(i) Roman one\n(ii) Roman two"})!;var labels=new List<string>();foreach(var x in parsed)labels.Add((string)x!.GetType().GetProperty("Label")!.GetValue(x)!);Check(labels.SequenceEqual(new[]{"(a)","(b)","(i)","(ii)"}),"parenthesized enumerators missing: "+string.Join(",",labels));Console.WriteLine("PASS PAREN ALPHA/ROMAN ITEMS");

// 8. Roman Article headings in AUTO mode.
var ra=Path.Combine(dir,"romanA.docx");var rb=Path.Combine(dir,"romanB.docx");Make(ra,P("ARTICLE I PURPOSE")+P("Alpha")+P("ARTICLE II TERM")+P("Beta"));Make(rb,P("ARTICLE I PURPOSE")+P("Alpha changed")+P("ARTICLE II TERM")+P("Beta"));var rc=await eng.CompareAsync(new[]{ra,rb},0,"auto",true,true);var nums=rc.Rows.Select(r=>r.Members[0]?.Number).Where(x=>x is not null).ToList();Check(nums.Contains("I")&&nums.Contains("II"),"Roman articles not legal units: "+string.Join(",",nums));Console.WriteLine("PASS ROMAN ARTICLES");

// 9. Consecutive tail deletions must keep A source order after the last B paragraph.
var tda=Path.Combine(dir,"tailA.docx");var tdb=Path.Combine(dir,"tailB.docx");var tdo=Path.Combine(dir,"tailOut.docx");
Make(tda,P("P1")+P("P2")+P("P3")+P("P4"));Make(tdb,P("P1")+P("P2"));await eng.ExportWordAsync(tda,tdb,tdo,"T",true);
var tdd=Doc(tdo);var tailTexts=tdd.Descendants(w+"p").Select(p=>string.Concat(p.Descendants(w+"t").Select(x=>x.Value))+string.Concat(p.Descendants(w+"delText").Select(x=>x.Value))).Where(x=>x.Length>0).ToList();
Check(tailTexts.SequenceEqual(new[]{"P1","P2","P3","P4"}),"tail deletion order regressed: "+string.Join(" | ",tailTexts));
Console.WriteLine("PASS TAIL DELETION ORDER");

// 10. Article renumber/move lineage must survive: old 7 -> new 6, not delete/new.
var la=Path.Combine(dir,"lineageA.docx");var lb=Path.Combine(dir,"lineageB.docx");
Make(la,P("Article 5 (Other)")+P("Stable five.")+P("Article 7 (Operational Policy)")+P("Same operational body.")+P("Article 9 (Final)")+P("Stable nine."));
Make(lb,P("Article 5 (Other)")+P("Stable five.")+P("Article 6 (Operation Policy)")+P("Same operational body.")+P("Article 9 (Final)")+P("Stable nine."));
var lc=await eng.CompareAsync(new[]{la,lb},0,"legal",true,true);var moved=lc.Rows.FirstOrDefault(r=>r.Members[0]?.Number=="7");
Check(moved?.Members[1]?.Number=="6","Article 7 did not map to Article 6");
Check(moved!.DisplayMessages.Any(x=>x.Contains("이동")||x.Contains("재번호화")),"Article renumber/move message missing");
Console.WriteLine("PASS ARTICLE 7 TO 6 LINEAGE");

// 11. Same logical ordinal with different notation must match ①/②/③ to 1./2./3.
var na=Path.Combine(dir,"notationA.docx");var nb=Path.Combine(dir,"notationB.docx");
Make(na,P("Article 9 (Obligations of the Company)")+P("① First obligation.")+P("② Second obligation.")+P("③ Third obligation."));
Make(nb,P("Article 9 (Obligations of the Company)")+P("1. First obligation.")+P("2. Second obligation.")+P("3. Third obligation."));
var nc=await eng.CompareAsync(new[]{na,nb},0,"legal",true,true);var nr=nc.Rows.Single(r=>r.Members[0]?.Number=="9");
Check(nr.DisplayMessages.Count(x=>x.Contains("표기")&&x.Contains("변경"))>=3,"logical ordinal notation matching regressed: "+string.Join(" | ",nr.DisplayMessages));
Check(!nr.DisplayMessages.Any(x=>x.Contains("조 신규")||x.Contains("조 삭제")),"notation change became article delete/add");
Console.WriteLine("PASS ARTICLE 9 NOTATION MATCH");

// 12. OpenXML schema validation for row revisions and document output.
foreach(var path in new[]{to,tio,ho}){using var wd=WordprocessingDocument.Open(path,false);var errors=new OpenXmlValidator().Validate(wd.MainDocumentPart!.Document).ToList();Check(errors.Count==0,"OpenXML validation failed for "+Path.GetFileName(path)+": "+string.Join(" | ",errors.Take(5).Select(e=>e.Description)));}Console.WriteLine("PASS WORD OPENXML VALIDATOR");

// 12. Deleting an entire earlier table must never redirect its text into the next surviving B table.
var tsa=Path.Combine(dir,"tableShiftA.docx");var tsb=Path.Combine(dir,"tableShiftB.docx");var tso=Path.Combine(dir,"tableShiftOut.docx");
Make(tsa,Tbl(Row("OLD_TABLE_A","OLD_TABLE_B"))+P("Between")+Tbl(Row("KEEP_A","KEEP_B")));
Make(tsb,P("Between")+Tbl(Row("KEEP_A","KEEP_B")));
await eng.ExportWordAsync(tsa,tsb,tso,"T",true);var tsd=Doc(tso);
var survivingCells=string.Join("|",tsd.Descendants(w+"tc").Select(tc=>string.Concat(tc.Descendants(w+"t").Select(x=>x.Value))));
Check(!survivingCells.Contains("OLD_TABLE"),"deleted table text leaked into a later surviving B table: "+survivingCells);
Check(string.Concat(tsd.Descendants(w+"delText").Select(x=>x.Value)).Contains("OLD_TABLE_A"),"deleted table fallback text missing");
Console.WriteLine("PASS DELETED TABLE INDEX SHIFT SAFETY");

// 13. A single (i) in an alphabetic sequence is alpha #9; in (i),(ii),(iii) it is roman #1.
var parse=typeof(NativeComparisonEngine).GetMethod("ParseParts",BindingFlags.Static|BindingFlags.NonPublic)!;
var hierarchy=typeof(NativeComparisonEngine).GetMethod("BuildPartHierarchy",BindingFlags.Static|BindingFlags.NonPublic)!;
object Parse(string value)=>parse.Invoke(null,new object[]{value})!;
List<string> Families(object parsedParts){var nodes=(System.Collections.IEnumerable)hierarchy.Invoke(null,new[]{parsedParts})!;var r=new List<string>();foreach(var node in nodes)r.Add((string)node!.GetType().GetProperty("Family")!.GetValue(node)!);return r;}
var alphaParts=Parse("(a) A\n(b) B\n(c) C\n(d) D\n(e) E\n(f) F\n(g) G\n(h) H\n(i) I\n(j) J");var alphaFamilies=Families(alphaParts);
Check(alphaFamilies.Count>=10&&alphaFamilies[8]=="paren-alpha","(i) was misclassified as roman inside alphabetic sequence: "+string.Join(",",alphaFamilies));
var romanParts=Parse("(i) One\n(ii) Two\n(iii) Three");var romanFamilies=Families(romanParts);
Check(romanFamilies.Count>=3&&romanFamilies[0]=="paren-roman"&&romanFamilies[1]=="paren-roman","roman (i),(ii) context was not recognized: "+string.Join(",",romanFamilies));
Console.WriteLine("PASS PAREN I CONTEXT DISAMBIGUATION");

// 14. Whole table-cell deletion/insertion must remain structural cell revisions.
string OneRow(params string[] cells)=>"<w:tr>"+string.Concat(cells.Select(x=>"<w:tc>"+P(x)+"</w:tc>"))+"</w:tr>";
var cda=Path.Combine(dir,"cellDelA.docx");var cdb=Path.Combine(dir,"cellDelB.docx");var cdo=Path.Combine(dir,"cellDelOut.docx");
Make(cda,Tbl(OneRow("KEEP","CELL_DELETED")));Make(cdb,Tbl(OneRow("KEEP")));await eng.ExportWordAsync(cda,cdb,cdo,"T",true);var cdd=Doc(cdo);var cdCells=cdd.Descendants(w+"tc").ToList();
Check(cdCells.Count==2,"deleted cell was not reconstructed");Check(cdCells[1].Element(w+"tcPr")?.Element(w+"cellDel") is not null,"deleted cell lacks tcPr/cellDel");Check(string.Concat(cdCells[1].Descendants(w+"t").Select(x=>x.Value)).Contains("CELL_DELETED"),"deleted cell text missing");
using(var wd=WordprocessingDocument.Open(cdo,false)){var errors=new OpenXmlValidator().Validate(wd.MainDocumentPart!.Document).ToList();Check(errors.Count==0,"deleted-cell OpenXML invalid: "+string.Join(" | ",errors.Take(5).Select(e=>e.Description)));}
var cia=Path.Combine(dir,"cellInsA.docx");var cib=Path.Combine(dir,"cellInsB.docx");var cio=Path.Combine(dir,"cellInsOut.docx");
Make(cia,Tbl(OneRow("KEEP")));Make(cib,Tbl(OneRow("KEEP","CELL_NEW")));await eng.ExportWordAsync(cia,cib,cio,"T",true);var cid=Doc(cio);var ciCells=cid.Descendants(w+"tc").ToList();
Check(ciCells.Count==2&&ciCells[1].Element(w+"tcPr")?.Element(w+"cellIns") is not null,"inserted cell lacks tcPr/cellIns");
using(var wd=WordprocessingDocument.Open(cio,false)){var errors=new OpenXmlValidator().Validate(wd.MainDocumentPart!.Document).ToList();Check(errors.Count==0,"inserted-cell OpenXML invalid: "+string.Join(" | ",errors.Take(5).Select(e=>e.Description)));}
Console.WriteLine("PASS TABLE CELL INSERT/DELETE");

// 15. Revisions hidden in ancillary Word parts and untracked ancillary text differences must not be silently preserved.
static void AddWordXml(string path,string name,string xml){using var fs=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);using var z=new ZipArchive(fs,ZipArchiveMode.Update);z.GetEntry(name)?.Delete();Put(z,name,xml);}
static void AddWordBytes(string path,string name,byte[] bytes){using var fs=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None);using var z=new ZipArchive(fs,ZipArchiveMode.Update);z.GetEntry(name)?.Delete();var e=z.CreateEntry(name);using var stream=e.Open();stream.Write(bytes);}
var hra=Path.Combine(dir,"headerRevA.docx");var hrb=Path.Combine(dir,"headerRevB.docx");var hro=Path.Combine(dir,"headerRevOut.docx");Make(hra,P("Body"));Make(hrb,P("Body"));
AddWordXml(hrb,"word/header1.xml","<w:hdr xmlns:w=\""+W+"\"><w:p><w:ins w:id=\"9\" w:author=\"Old\"><w:r><w:t>OLD HEADER REV</w:t></w:r></w:ins></w:p></w:hdr>");
var headerRevisionBlocked=false;try{await eng.ExportWordAsync(hra,hrb,hro,"T",true);}catch(InvalidOperationException ex){headerRevisionBlocked=ex.Message.Contains("header1.xml")&&ex.Message.Contains("기존 Word 변경추적");}Check(headerRevisionBlocked,"header tracked revision was not rejected");
var hda=Path.Combine(dir,"headerDiffA.docx");var hdb=Path.Combine(dir,"headerDiffB.docx");var hdo=Path.Combine(dir,"headerDiffOut.docx");Make(hda,P("Body"));Make(hdb,P("Body"));
AddWordXml(hda,"word/header1.xml","<w:hdr xmlns:w=\""+W+"\"><w:p><w:r><w:t>HEADER A</w:t></w:r></w:p></w:hdr>");
AddWordXml(hdb,"word/header1.xml","<w:hdr xmlns:w=\""+W+"\"><w:p><w:r><w:t>HEADER B</w:t></w:r></w:p></w:hdr>");
var headerDiffBlocked=false;try{await eng.ExportWordAsync(hda,hdb,hdo,"T",true);}catch(InvalidOperationException ex){headerDiffBlocked=ex.Message.Contains("header")&&ex.Message.Contains("서로 다릅니다");}Check(headerDiffBlocked,"header text difference was silently omitted");
Console.WriteLine("PASS ANCILLARY WORD PART GUARDS");

// 16. UInt128 Indel fast path must be bit-for-bit equivalent to ordinary LCS for 65..128 chars.
var indel=typeof(NativeComparisonEngine).GetMethod("IndelRatio",BindingFlags.Static|BindingFlags.NonPublic)!;
static double NaiveLcsRatio(string a,string b){var dp=new int[a.Length+1,b.Length+1];for(var i=a.Length-1;i>=0;i--)for(var j=b.Length-1;j>=0;j--)dp[i,j]=a[i]==b[j]?dp[i+1,j+1]+1:Math.Max(dp[i+1,j],dp[i,j+1]);return 2.0*dp[0,0]/Math.Max(1,a.Length+b.Length);}
foreach(var n in new[]{65,96,127,128,129}){var aa=string.Concat(Enumerable.Range(0,n).Select(i=>(char)('a'+(i%7))));var bb=new string(aa.Reverse().ToArray());var got=(double)indel.Invoke(null,new object[]{aa,bb})!;var expected=NaiveLcsRatio(aa,bb);Check(Math.Abs(got-expected)<1e-12,$"Indel parity failed n={n}: {got} != {expected}");}
Console.WriteLine("PASS UINT128/BIGINTEGER INDEL PARITY");

// 17. Flattened single-line English lists must use the same (i) context rule as normal line lists.
var flatAlpha=(System.Collections.IEnumerable)parse.Invoke(null,new object[]{"Lead: (a) A (b) B (c) C (d) D (e) E (f) F (g) G (h) H (i) I (j) J"})!;
var flatAlphaLabels=new List<string>();foreach(var x in flatAlpha)flatAlphaLabels.Add((string)x!.GetType().GetProperty("Label")!.GetValue(x)!);
Check(flatAlphaLabels.Contains("(i)")&&flatAlphaLabels.Contains("(j)"),"flattened alpha list boundaries not recovered: "+string.Join(",",flatAlphaLabels));
var flatRoman=(System.Collections.IEnumerable)parse.Invoke(null,new object[]{"Lead: (i) One (ii) Two (iii) Three"})!;
var flatRomanLabels=new List<string>();foreach(var x in flatRoman)flatRomanLabels.Add((string)x!.GetType().GetProperty("Label")!.GetValue(x)!);
Check(flatRomanLabels.Contains("(i)")&&flatRomanLabels.Contains("(ii)")&&flatRomanLabels.Contains("(iii)"),"flattened Roman list boundaries not recovered: "+string.Join(",",flatRomanLabels));
Console.WriteLine("PASS FLATTENED PAREN I CONTEXT");

// 18. Same header text moved to a different package part is not silently treated as equivalent.
var hpa=Path.Combine(dir,"headerPartA.docx");var hpb=Path.Combine(dir,"headerPartB.docx");var hpo=Path.Combine(dir,"headerPartOut.docx");Make(hpa,P("Body"));Make(hpb,P("Body"));
AddWordXml(hpa,"word/header1.xml","<w:hdr xmlns:w=\""+W+"\"><w:p><w:r><w:t>SAME HEADER</w:t></w:r></w:p></w:hdr>");
AddWordXml(hpb,"word/header2.xml","<w:hdr xmlns:w=\""+W+"\"><w:p><w:r><w:t>SAME HEADER</w:t></w:r></w:p></w:hdr>");
var headerPartBlocked=false;try{await eng.ExportWordAsync(hpa,hpb,hpo,"T",true);}catch(InvalidOperationException ex){headerPartBlocked=ex.Message.Contains("내용/위치")&&ex.Message.Contains("header");}Check(headerPartBlocked,"header part relocation was silently ignored");
Console.WriteLine("PASS ANCILLARY PART LOCATION GUARD");


// 19. XLSX must remain a schema-valid text-only export even with XML 1.0 control characters in source TXT.
var xa=Path.Combine(dir,"controlA.txt");var xb=Path.Combine(dir,"controlB.txt");var xo=Path.Combine(dir,"validated.xlsx");
File.WriteAllText(xa,"Alpha\u000B old\nSecond",new UTF8Encoding(false));File.WriteAllText(xb,"Alpha\u000C new\nSecond",new UTF8Encoding(false));
var xc=await eng.CompareAsync(new[]{xa,xb},0,"general",true,true);await eng.ExportExcelAsync(xc,xo);
using(var ss=SpreadsheetDocument.Open(xo,false)){var errors=new OpenXmlValidator().Validate(ss).ToList();Check(errors.Count==0,"XLSX package invalid: "+string.Join(" | ",errors.Take(8).Select(e=>e.Description)));}
using(var z=ZipFile.OpenRead(xo)){using var sr=new StreamReader(z.GetEntry("xl/worksheets/sheet1.xml")!.Open());var xml=sr.ReadToEnd();Check(!xml.Contains('\u000B')&&!xml.Contains('\u000C'),"illegal XML control survived XLSX export");Check(!xml.Contains("w:tbl")&&!xml.Contains("TableGrid"),"Word formatting leaked into XLSX");}
Console.WriteLine("PASS XLSX PACKAGE/XML SAFETY");

// 20. Zero-width formatting before legal item numbers must not swallow Article 4 item structure.
var zwa=Path.Combine(dir,"zeroWidthA.docx");var zwb=Path.Combine(dir,"zeroWidthB.docx");
Make(zwa,P("Article 4 (Items)")+P("\u200B1. One")+P("\u200B2. Two")+P("\u200B3. Three")+P("\u200B4. Four")+P("\u200B5. Five"));
Make(zwb,P("Article 4 (Items)")+P("1. One")+P("2. Two changed")+P("3. Three")+P("4. Four")+P("5. Five"));
var zwc=await eng.CompareAsync(new[]{zwa,zwb},0,"legal",true,true);var zwr=zwc.Rows.Single(r=>r.Members[0]?.Number=="4");
Check(!zwr.DisplayMessages.Any(x=>x.Contains("조 삭제")||x.Contains("조 신규")),"zero-width Article 4 lineage regressed");Check(zwr.Changed,"zero-width Article 4 wording change was missed");
Console.WriteLine("PASS ZERO-WIDTH ARTICLE 4");

// 21. Collapsed adjacent hierarchy may cross-match, while a healthy nested hierarchy must stay nested.
var cla=Path.Combine(dir,"collapsedA.docx");var clb=Path.Combine(dir,"collapsedB.docx");
Make(cla,P("Article 10 (Structure)")+P("① Wrapper")+P("1. First child")+P("2. Second child"));
Make(clb,P("Article 10 (Structure)")+P("(1) First child")+P("(2) Second child"));
var clc=await eng.CompareAsync(new[]{cla,clb},0,"legal",true,true);var clr=clc.Rows.Single(r=>r.Members[0]?.Number=="10");
Check(clr.DisplayMessages.Any(x=>x.Contains("레벨")||x.Contains("구조")),"collapsed hierarchy change not reported");
var hna=Path.Combine(dir,"healthyA.docx");var hnb=Path.Combine(dir,"healthyB.docx");
Make(hna,P("Article 10 (Structure)")+P("① Wrapper")+P("1. First child")+P("2. Second child"));
Make(hnb,P("Article 10 (Structure)")+P("① Wrapper")+P("(1) First child")+P("(2) Second child"));
var hnc=await eng.CompareAsync(new[]{hna,hnb},0,"legal",true,true);var hnr=hnc.Rows.Single(r=>r.Members[0]?.Number=="10");
Check(!hnr.DisplayMessages.Any(x=>x.Contains("① → (1)")),"healthy hierarchy was flattened into cross-level match");
Console.WriteLine("PASS COLLAPSED/HEALTHY HIERARCHY");

// 22. Word numbering lvlOverride and lvlRestart semantics.
static string NumP(int lvl,string text)=>$"<w:p><w:pPr><w:numPr><w:ilvl w:val=\"{lvl}\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr><w:r><w:t>{text}</w:t></w:r></w:p>";
static void MakeNumbered(string path,int restart,string Wns){using var fs=new FileStream(path,FileMode.Create);using var z=new ZipArchive(fs,ZipArchiveMode.Create);Put(z,"[Content_Types].xml","<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");Put(z,"word/document.xml","<?xml version=\"1.0\"?><w:document xmlns:w=\""+Wns+"\"><w:body>"+NumP(0,"Parent1")+NumP(1,"Child1")+NumP(1,"Child2")+NumP(0,"Parent2")+NumP(1,"Child3")+"<w:sectPr/></w:body></w:document>");Put(z,"word/numbering.xml",$"<?xml version=\"1.0\"?><w:numbering xmlns:w=\"{Wns}\"><w:abstractNum w:abstractNumId=\"0\"><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%1.\"/></w:lvl><w:lvl w:ilvl=\"1\"><w:start w:val=\"1\"/><w:numFmt w:val=\"lowerLetter\"/><w:lvlText w:val=\"%1.%2)\"/></w:lvl></w:abstractNum><w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/><w:lvlOverride w:ilvl=\"1\"><w:lvl w:ilvl=\"1\"><w:start w:val=\"3\"/><w:numFmt w:val=\"lowerLetter\"/><w:lvlText w:val=\"%1.%2)\"/><w:lvlRestart w:val=\"{restart}\"/></w:lvl></w:lvlOverride></w:num></w:numbering>");}
var n1=Path.Combine(dir,"numberRestart1.docx");MakeNumbered(n1,1,W);var n1t=await Read(n1);Check(n1t.Contains("1.c) Child1")&&n1t.Contains("1.d) Child2")&&n1t.Contains("2.c) Child3"),"lvlRestart=1 failed: "+n1t);
var n0=Path.Combine(dir,"numberRestart0.docx");MakeNumbered(n0,0,W);var n0t=await Read(n0);Check(n0t.Contains("1.c) Child1")&&n0t.Contains("1.d) Child2")&&n0t.Contains("2.e) Child3"),"lvlRestart=0 failed: "+n0t);
Console.WriteLine("PASS NUMBERING OVERRIDE/RESTART");


// 23. BOM-less pure Korean UTF-16 LE/BE must decode correctly without stealing valid CP949.
var pure="개인정보처리방침개정내용";
var kle=Path.Combine(dir,"pure-korean-le.txt");var kbe=Path.Combine(dir,"pure-korean-be.txt");
File.WriteAllBytes(kle,new UnicodeEncoding(false,false,true).GetBytes(pure));File.WriteAllBytes(kbe,new UnicodeEncoding(true,false,true).GetBytes(pure));
Check(await Read(kle)==pure,"pure Korean UTF-16LE without BOM failed");Check(await Read(kbe)==pure,"pure Korean UTF-16BE without BOM failed");
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);var kcp=Path.Combine(dir,"cp949.txt");File.WriteAllBytes(kcp,Encoding.GetEncoding(949).GetBytes(pure));
Check(await Read(kcp)==pure,"valid CP949 was misdetected as UTF-16");
Console.WriteLine("PASS PURE-KOREAN BOMLESS UTF16 + CP949");


// 24. Property-format Track Changes (pPrChange/rPrChange/etc.) must be rejected before mixing histories.
var pra=Path.Combine(dir,"propertyRevA.docx");var prb=Path.Combine(dir,"propertyRevB.docx");var pro=Path.Combine(dir,"propertyRevOut.docx");
Make(pra,P("Alpha"));
Make(prb,"<w:p><w:pPr><w:jc w:val=\"center\"/><w:pPrChange w:id=\"42\" w:author=\"Old\"><w:pPr><w:jc w:val=\"left\"/></w:pPr></w:pPrChange></w:pPr><w:r><w:t>Alpha changed</w:t></w:r></w:p>");
var propertyRevisionBlocked=false;try{await eng.ExportWordAsync(pra,prb,pro,"Current",true);}catch(InvalidOperationException ex){propertyRevisionBlocked=ex.Message.Contains("기존 Word 변경추적");}
Check(propertyRevisionBlocked,"pPrChange was not rejected as an existing tracked revision");
Console.WriteLine("PASS PROPERTY REVISION GUARD");

// 25. Table row/cell SDT and customXml wrappers are transparent visible structure, not data-loss boundaries.
var rowSdt=Path.Combine(dir,"rowSdt.docx");var cellSdt=Path.Combine(dir,"cellSdt.docx");var customRow=Path.Combine(dir,"customRow.docx");
Make(rowSdt,"<w:tbl><w:tblPr/><w:tblGrid><w:gridCol/></w:tblGrid><w:sdt><w:sdtPr/><w:sdtContent><w:tr><w:tc>"+P("SDT_ROW_CELL")+"</w:tc></w:tr></w:sdtContent></w:sdt></w:tbl>");
Make(cellSdt,"<w:tbl><w:tblPr/><w:tblGrid><w:gridCol/></w:tblGrid><w:tr><w:sdt><w:sdtPr/><w:sdtContent><w:tc>"+P("SDT_CELL")+"</w:tc></w:sdtContent></w:sdt></w:tr></w:tbl>");
Make(customRow,"<w:tbl><w:tblPr/><w:tblGrid><w:gridCol/></w:tblGrid><w:customXml><w:tr><w:tc>"+P("CUSTOM_ROW")+"</w:tc></w:tr></w:customXml></w:tbl>");
Check((await Read(rowSdt)).Contains("SDT_ROW_CELL"),"row-level SDT table text disappeared");
Check((await Read(cellSdt)).Contains("SDT_CELL"),"cell-level SDT table text disappeared");
Check((await Read(customRow)).Contains("CUSTOM_ROW"),"customXml-wrapped table row disappeared");
Console.WriteLine("PASS TABLE SDT/CUSTOMXML VISIBILITY");

// 26. Number-only change must create a valid Word numbering revision, not UI-only change.
static void MakeSingleNumbered(string path,int start,string Wns){using var fs=new FileStream(path,FileMode.Create);using var z=new ZipArchive(fs,ZipArchiveMode.Create);Put(z,"[Content_Types].xml","<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/><Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/></Types>");Put(z,"_rels/.rels","<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");Put(z,"word/_rels/document.xml.rels","<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdNum\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/></Relationships>");Put(z,"word/document.xml","<?xml version=\"1.0\"?><w:document xmlns:w=\""+Wns+"\"><w:body><w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr><w:r><w:t>Same item</w:t></w:r></w:p><w:sectPr/></w:body></w:document>");Put(z,"word/numbering.xml",$"<?xml version=\"1.0\"?><w:numbering xmlns:w=\"{Wns}\"><w:abstractNum w:abstractNumId=\"0\"><w:lvl w:ilvl=\"0\"><w:start w:val=\"{start}\"/><w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%1.\"/></w:lvl></w:abstractNum><w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num></w:numbering>");}
var numA=Path.Combine(dir,"numberOnlyA.docx");var numB=Path.Combine(dir,"numberOnlyB.docx");var numOut=Path.Combine(dir,"numberOnlyOut.docx");MakeSingleNumbered(numA,1,W);MakeSingleNumbered(numB,2,W);
var numCmp=await eng.CompareAsync(new[]{numA,numB},0,"general",true,true);Check(numCmp.Rows.Any(r=>r.Changed),"number-only change was not visible in comparison");
await eng.ExportWordAsync(numA,numB,numOut,"T",true,default,numCmp,0,1);var numDoc=Doc(numOut);var numChange=numDoc.Descendants(w+"numberingChange").SingleOrDefault();
Check(numChange is not null&&numChange.Attribute(w+"original")?.Value=="1.","number-only change did not emit w:numberingChange with original label");
using(var wd=WordprocessingDocument.Open(numOut,false)){var errors=new OpenXmlValidator().Validate(wd.MainDocumentPart!.Document).ToList();Check(errors.Count==0,"numberingChange OpenXML invalid: "+string.Join(" | ",errors.Take(5).Select(e=>e.Description)));}
Console.WriteLine("PASS WORD NUMBERING CHANGE TRACKING");
// 26b. Numbering insertion/removal on an existing paragraph must be tracked and schema-valid.
var numPlain=Path.Combine(dir,"numberPlain.docx");Make(numPlain,P("Same item"));
var numAdded=Path.Combine(dir,"numberAddedOut.docx");var addCmp=await eng.CompareAsync(new[]{numPlain,numB},0,"general",true,true);await eng.ExportWordAsync(numPlain,numB,numAdded,"T",true,default,addCmp,0,1);var addDoc=Doc(numAdded);Check(addDoc.Descendants(w+"numPr").Any(np=>np.Element(w+"ins") is not null),"numbering insertion lacked numPr/w:ins");
using(var wd=WordprocessingDocument.Open(numAdded,false)){var errors=new OpenXmlValidator().Validate(wd.MainDocumentPart!.Document).ToList();Check(errors.Count==0,"numbering insertion OpenXML invalid: "+string.Join(" | ",errors.Take(5).Select(e=>e.Description)));}
var numRemoved=Path.Combine(dir,"numberRemovedOut.docx");var removeCmp=await eng.CompareAsync(new[]{numA,numPlain},0,"general",true,true);await eng.ExportWordAsync(numA,numPlain,numRemoved,"T",true,default,removeCmp,0,1);var removeDoc=Doc(numRemoved);var removedChange=removeDoc.Descendants(w+"numberingChange").SingleOrDefault();Check(removedChange is not null&&removedChange.Attribute(w+"original")?.Value=="1.","numbering removal lacked numberingChange original label");
using(var wd=WordprocessingDocument.Open(numRemoved,false)){var errors=new OpenXmlValidator().Validate(wd.MainDocumentPart!.Document).ToList();Check(errors.Count==0,"numbering removal OpenXML invalid: "+string.Join(" | ",errors.Take(5).Select(e=>e.Description)));}
Console.WriteLine("PASS WORD NUMBERING INSERT/REMOVE");

// 27. Roman article parser must accept canonical numerals but reject ordinary Roman-letter words.
var parseUnits=typeof(NativeComparisonEngine).GetMethod("ParseUnits",BindingFlags.Static|BindingFlags.NonPublic)!;
var falseRomans=(System.Collections.IEnumerable)parseUnits.Invoke(null,new object[]{"Article CIVIL Rights\nBody one\nArticle IIV Terms\nBody two\nArticle VX End\nBody three","auto"})!;
var falseNums=new List<string>();foreach(var u in falseRomans){var number=(string)u!.GetType().GetProperty("Number")!.GetValue(u)!;if(number.Length>0)falseNums.Add(number);}
Check(!falseNums.Contains("CIVIL")&&!falseNums.Contains("IIV")&&!falseNums.Contains("VX"),"non-canonical Roman text became article numbers: "+string.Join(",",falseNums));
var trueRomans=(System.Collections.IEnumerable)parseUnits.Invoke(null,new object[]{"ARTICLE IV TERM\nAlpha\nARTICLE IX END\nBeta\nARTICLE MIX HIGH NUMBER\nGamma","auto"})!;var trueNums=new List<string>();foreach(var u in trueRomans){var number=(string)u!.GetType().GetProperty("Number")!.GetValue(u)!;if(number.Length>0)trueNums.Add(number);}
Check(trueNums.Contains("IV")&&trueNums.Contains("IX")&&trueNums.Contains("MIX"),"canonical Roman articles stopped parsing: "+string.Join(",",trueNums));
Console.WriteLine("PASS CANONICAL ROMAN ARTICLE FILTER");
var genericRoman=(System.Collections.IEnumerable)parseUnits.Invoke(null,new object[]{"IIV Invalid heading\nBody\nIX Valid heading\nBody","general"})!;
var genericNums=new List<string>();foreach(var u in genericRoman){var number=(string)u!.GetType().GetProperty("Number")!.GetValue(u)!;if(number.Length>0)genericNums.Add(number);}
Check(!genericNums.Contains("IIV")&&genericNums.Contains("IX"),"generic heading Roman validation regressed: "+string.Join(",",genericNums));
Console.WriteLine("PASS GENERIC ROMAN HEADING VALIDATION");

// 28. SpreadsheetML rich text properties use deterministic strike -> color -> underline order.
using(var z=ZipFile.OpenRead(xo)){using var sr=new StreamReader(z.GetEntry("xl/worksheets/sheet1.xml")!.Open());var xml=sr.ReadToEnd();Check(!xml.Contains("<rPr><u val=\"single\"/><color"),"insert rPr regressed to underline-before-color");Check(!xml.Contains("<rPr><strike/><u val=\"single\"/><color"),"both rPr regressed to underline-before-color");Check(xml.Contains("<rPr><color rgb=\"FF1565C0\"/><u val=\"single\"/></rPr>"),"insert color->underline order missing");}
Console.WriteLine("PASS XLSX RPR ORDER");

// 29. Large Word paragraph gaps use linear-space similarity alignment, not blind positional pairing.
var largeA=Path.Combine(dir,"largeGapA.txt");var largeB=Path.Combine(dir,"largeGapB.docx");var largeOut=Path.Combine(dir,"largeGapOut.docx");
var largeOld=Enumerable.Range(0,505).Select(i=>$"Clause {i:D3} stable unique wording").ToList();File.WriteAllLines(largeA,largeOld,new UTF8Encoding(false));
var largeBody=P("UNRELATED LEADING PARAGRAPH")+string.Concat(largeOld.Select((x,i)=>P(x+" revised")));Make(largeB,largeBody);
await eng.ExportWordAsync(largeA,largeB,largeOut,"T",true);var largeDoc=Doc(largeOut);var firstP=largeDoc.Descendants(w+"p").First();
Check(firstP.Descendants(w+"ins").Any()&&firstP.Element(w+"pPr")?.Element(w+"rPr")?.Element(w+"ins") is not null,"large-gap alignment blindly paired the unrelated leading paragraph");
Console.WriteLine("PASS LARGE-GAP LINEAR-SPACE ALIGNMENT");


// 30. In-place Word export must preserve a row-level SDT wrapper while tracking text inside it.
var sdtExpA=Path.Combine(dir,"sdtExportA.docx");var sdtExpB=Path.Combine(dir,"sdtExportB.docx");var sdtExpO=Path.Combine(dir,"sdtExportOut.docx");
string SdtTable(string value)=>"<w:tbl><w:tblPr/><w:tblGrid><w:gridCol/></w:tblGrid><w:sdt><w:sdtPr><w:tag w:val=\"locked-row\"/></w:sdtPr><w:sdtContent><w:tr><w:tc>"+P(value)+"</w:tc></w:tr></w:sdtContent></w:sdt></w:tbl>";
Make(sdtExpA,SdtTable("Controlled old wording"));Make(sdtExpB,SdtTable("Controlled new wording"));await eng.ExportWordAsync(sdtExpA,sdtExpB,sdtExpO,"T",true);var sdtOut=Doc(sdtExpO);
Check(sdtOut.Descendants(w+"sdt").Any(),"row SDT wrapper was destroyed during export");Check(sdtOut.Descendants(w+"delText").Any(x=>x.Value.Contains("old")),"row SDT deletion missing");Check(sdtOut.Descendants(w+"ins").Descendants(w+"t").Any(x=>x.Value.Contains("new")),"row SDT insertion missing");
using(var wd=WordprocessingDocument.Open(sdtExpO,false)){var errors=new OpenXmlValidator().Validate(wd.MainDocumentPart!.Document).ToList();Check(errors.Count==0,"SDT tracked export OpenXML invalid: "+string.Join(" | ",errors.Take(5).Select(e=>e.Description)));}
Console.WriteLine("PASS SDT IN-PLACE WORD EXPORT");


// 31. Repeated numbered paragraphs must anchor by logical label+body, so only the actually renumbered duplicate changes.
static void MakeRepeatedNumbered(string path,int secondStart,string Wns)
{
    using var fs=new FileStream(path,FileMode.Create);using var z=new ZipArchive(fs,ZipArchiveMode.Create);
    Put(z,"[Content_Types].xml","<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/><Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/></Types>");
    Put(z,"_rels/.rels","<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
    Put(z,"word/_rels/document.xml.rels","<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdNum\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/></Relationships>");
    string NP(int id)=>$"<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"{id}\"/></w:numPr></w:pPr><w:r><w:t>Same item</w:t></w:r></w:p>";
    Put(z,"word/document.xml","<?xml version=\"1.0\"?><w:document xmlns:w=\""+Wns+"\"><w:body>"+NP(1)+NP(2)+"<w:sectPr/></w:body></w:document>");
    Put(z,"word/numbering.xml",$"<?xml version=\"1.0\"?><w:numbering xmlns:w=\"{Wns}\"><w:abstractNum w:abstractNumId=\"0\"><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%1.\"/></w:lvl></w:abstractNum><w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num><w:num w:numId=\"2\"><w:abstractNumId w:val=\"0\"/><w:lvlOverride w:ilvl=\"0\"><w:startOverride w:val=\"{secondStart}\"/></w:lvlOverride></w:num></w:numbering>");
}
var repA=Path.Combine(dir,"repeatedNumberA.docx");var repB=Path.Combine(dir,"repeatedNumberB.docx");var repO=Path.Combine(dir,"repeatedNumberOut.docx");
MakeRepeatedNumbered(repA,2,W);MakeRepeatedNumbered(repB,3,W);
var repAText=await Read(repA);var repBText=await Read(repB);
Check(repAText.Contains("1. Same item")&&repAText.Contains("2. Same item"),"repeated A numbering labels wrong: "+repAText);
Check(repBText.Contains("1. Same item")&&repBText.Contains("3. Same item"),"repeated B numbering labels wrong: "+repBText);
var repCmp=await eng.CompareAsync(new[]{repA,repB},0,"general",true,true);
await eng.ExportWordAsync(repA,repB,repO,"T",true,default,repCmp,0,1);
var repDoc=Doc(repO);var repParas=repDoc.Descendants(w+"p").Where(p=>p.Descendants(w+"t").Any(t=>t.Value=="Same item")).ToList();
Check(repParas.Count==2,"repeated numbered paragraph count changed");
Check(!repParas[0].Descendants(w+"numberingChange").Any(),"unchanged first duplicate received numberingChange");
var repChange=repParas[1].Descendants(w+"numberingChange").SingleOrDefault();
Check(repChange is not null&&repChange.Attribute(w+"original")?.Value=="2.","renumbered second duplicate was anchored to the wrong paragraph");
using(var wd=WordprocessingDocument.Open(repO,false)){var errors=new OpenXmlValidator().Validate(wd.MainDocumentPart!.Document).ToList();Check(errors.Count==0,"repeated-number anchor OpenXML invalid: "+string.Join(" | ",errors.Take(5).Select(e=>e.Description)));}
Console.WriteLine("PASS REPEATED NUMBERING LOGICAL ANCHOR");

// 32. NextRevisionId must skip every revision id family, including property changes and table/numbering revisions.
var exporterType=typeof(NativeComparisonEngine).Assembly.GetType("DocumentCompare.Avalonia.Engine.NativeOfficeExporter")!;
var nextRevision=exporterType.GetMethod("NextRevisionId",BindingFlags.Static|BindingFlags.NonPublic)!;
var revProbe=new XDocument(new XElement(w+"document",new XElement(w+"body",
    new XElement(w+"ins",new XAttribute(w+"id","3")),
    new XElement(w+"del",new XAttribute(w+"id","7")),
    new XElement(w+"moveFrom",new XAttribute(w+"id","11")),
    new XElement(w+"moveTo",new XAttribute(w+"id","13")),
    new XElement(w+"cellIns",new XAttribute(w+"id","31")),
    new XElement(w+"cellDel",new XAttribute(w+"id","37")),
    new XElement(w+"numberingChange",new XAttribute(w+"id","41")),
    new XElement(w+"rPrChange",new XAttribute(w+"id","52")),
    new XElement(w+"pPrChange",new XAttribute(w+"id","55")))));
var nextId=(int)nextRevision.Invoke(null,new object[]{revProbe,w})!;
Check(nextId==56,"NextRevisionId failed to skip all existing revision ids: "+nextId);
Console.WriteLine("PASS REVISION ID COLLISION SAFETY");

// 33. SDT/customXml wrapped table rows must keep B wrappers on insertion, and deleted wrapped A rows must reappear as valid structural row deletions.
string Tbl1(string rows)=>"<w:tbl><w:tblPr/><w:tblGrid><w:gridCol/></w:tblGrid>"+rows+"</w:tbl>";
string Row1(string value)=>"<w:tr><w:tc>"+P(value)+"</w:tc></w:tr>";
string SdtRow1(string value)=>"<w:sdt><w:sdtPr><w:tag w:val=\"row-sdt\"/></w:sdtPr><w:sdtContent>"+Row1(value)+"</w:sdtContent></w:sdt>";
string CustomRow1(string value)=>"<w:customXml>"+Row1(value)+"</w:customXml>";
var wrapInsA=Path.Combine(dir,"wrappedRowsInsA.docx");var wrapInsB=Path.Combine(dir,"wrappedRowsInsB.docx");var wrapInsO=Path.Combine(dir,"wrappedRowsInsOut.docx");
Make(wrapInsA,Tbl1(Row1("KEEP")));Make(wrapInsB,Tbl1(Row1("KEEP")+SdtRow1("SDT_NEW")+CustomRow1("CUSTOM_NEW")));
await eng.ExportWordAsync(wrapInsA,wrapInsB,wrapInsO,"T",true);var wrapInsDoc=Doc(wrapInsO);
var sdtInsertedRow=wrapInsDoc.Descendants(w+"sdt").Descendants(w+"tr").Single(r=>r.Value.Contains("SDT_NEW"));
var customInsertedRow=wrapInsDoc.Descendants(w+"customXml").Descendants(w+"tr").Single(r=>r.Value.Contains("CUSTOM_NEW"));
Check(sdtInsertedRow.Element(w+"trPr")?.Element(w+"ins") is not null,"SDT wrapped inserted row lacks trPr/ins");
Check(customInsertedRow.Element(w+"trPr")?.Element(w+"ins") is not null,"customXml wrapped inserted row lacks trPr/ins");
using(var wd=WordprocessingDocument.Open(wrapInsO,false)){var errors=new OpenXmlValidator().Validate(wd.MainDocumentPart!.Document).ToList();Check(errors.Count==0,"wrapped row insertion OpenXML invalid: "+string.Join(" | ",errors.Take(5).Select(e=>e.Description)));}
var wrapDelA=Path.Combine(dir,"wrappedRowsDelA.docx");var wrapDelB=Path.Combine(dir,"wrappedRowsDelB.docx");var wrapDelO=Path.Combine(dir,"wrappedRowsDelOut.docx");
Make(wrapDelA,Tbl1(Row1("KEEP")+SdtRow1("SDT_OLD")+CustomRow1("CUSTOM_OLD")));Make(wrapDelB,Tbl1(Row1("KEEP")));
await eng.ExportWordAsync(wrapDelA,wrapDelB,wrapDelO,"T",true);var wrapDelDoc=Doc(wrapDelO);
var deletedRows=wrapDelDoc.Descendants(w+"tr").Where(r=>r.Element(w+"trPr")?.Element(w+"del") is not null).ToList();
Check(deletedRows.Count>=2&&deletedRows.Any(r=>r.Value.Contains("SDT_OLD"))&&deletedRows.Any(r=>r.Value.Contains("CUSTOM_OLD")),"wrapped deleted rows were not reconstructed as row deletions");
using(var wd=WordprocessingDocument.Open(wrapDelO,false)){var errors=new OpenXmlValidator().Validate(wd.MainDocumentPart!.Document).ToList();Check(errors.Count==0,"wrapped row deletion OpenXML invalid: "+string.Join(" | ",errors.Take(5).Select(e=>e.Description)));}
Console.WriteLine("PASS WRAPPED TABLE ROW REVISION EXPORT");

// 34. Nested-table text keeps document order, while Word export targets the inner table rather than corrupting outer-cell paragraphs.
string NestedTable(string inner)=>"<w:tbl><w:tblPr/><w:tblGrid><w:gridCol/></w:tblGrid><w:tr><w:tc>"+P("OUTER_BEFORE")+"<w:tbl><w:tblPr/><w:tblGrid><w:gridCol/></w:tblGrid><w:tr><w:tc>"+P(inner)+"</w:tc></w:tr></w:tbl>"+P("OUTER_AFTER")+"</w:tc></w:tr></w:tbl>";
var nestedA=Path.Combine(dir,"nestedA.docx");var nestedB=Path.Combine(dir,"nestedB.docx");var nestedO=Path.Combine(dir,"nestedOut.docx");
Make(nestedA,NestedTable("INNER_OLD"));Make(nestedB,NestedTable("INNER_NEW"));
var nestedText=await Read(nestedA);var beforePos=nestedText.IndexOf("OUTER_BEFORE",StringComparison.Ordinal);var innerPos=nestedText.IndexOf("INNER_OLD",StringComparison.Ordinal);var afterPos=nestedText.IndexOf("OUTER_AFTER",StringComparison.Ordinal);
Check(beforePos>=0&&innerPos>beforePos&&afterPos>innerPos,"nested-table reader order regressed: "+nestedText);
await eng.ExportWordAsync(nestedA,nestedB,nestedO,"T",true);var nestedDoc=Doc(nestedO);var nestedTables=nestedDoc.Descendants(w+"tbl").ToList();Check(nestedTables.Count==2,"nested table structure changed");
var innerTable=nestedTables[1];var innerRows=innerTable.Descendants(w+"tr").ToList();
Check(innerRows.Any(r=>r.Value.Contains("INNER_OLD")&&r.Element(w+"trPr")?.Element(w+"del") is not null),"inner-table deletion was not tracked as a deleted inner row");
Check(innerRows.Any(r=>r.Value.Contains("INNER_NEW")&&r.Element(w+"trPr")?.Element(w+"ins") is not null),"inner-table insertion was not tracked as an inserted inner row");
var outerBefore=nestedDoc.Descendants(w+"p").First(p=>p.Descendants(w+"t").Any(t=>t.Value=="OUTER_BEFORE"));var outerAfter=nestedDoc.Descendants(w+"p").First(p=>p.Descendants(w+"t").Any(t=>t.Value=="OUTER_AFTER"));
Check(!outerBefore.Descendants(w+"ins").Any()&&!outerBefore.Descendants(w+"del").Any()&&!outerAfter.Descendants(w+"ins").Any()&&!outerAfter.Descendants(w+"del").Any(),"inner-table change polluted outer-cell paragraphs");
using(var wd=WordprocessingDocument.Open(nestedO,false)){var errors=new OpenXmlValidator().Validate(wd.MainDocumentPart!.Document).ToList();Check(errors.Count==0,"nested-table export OpenXML invalid: "+string.Join(" | ",errors.Take(5).Select(e=>e.Description)));}
Console.WriteLine("PASS NESTED TABLE EXPORT TARGETING");

// 35. Cell-level SDT/customXml wrappers must likewise receive structural cell revisions without losing B wrappers.
string Cell1(string value)=>"<w:tc>"+P(value)+"</w:tc>";
string SdtCell1(string value)=>"<w:sdt><w:sdtPr/><w:sdtContent>"+Cell1(value)+"</w:sdtContent></w:sdt>";
string CustomCell1(string value)=>"<w:customXml>"+Cell1(value)+"</w:customXml>";
string RowCells1(string cells)=>"<w:tr>"+cells+"</w:tr>";
var cellWrapInsA=Path.Combine(dir,"wrappedCellInsA.docx");var cellWrapInsB=Path.Combine(dir,"wrappedCellInsB.docx");var cellWrapInsO=Path.Combine(dir,"wrappedCellInsOut.docx");
Make(cellWrapInsA,Tbl1(RowCells1(Cell1("KEEP"))));Make(cellWrapInsB,Tbl1(RowCells1(Cell1("KEEP")+SdtCell1("SDT_CELL_NEW"))));
await eng.ExportWordAsync(cellWrapInsA,cellWrapInsB,cellWrapInsO,"T",true);var cellWrapInsDoc=Doc(cellWrapInsO);
var sdtInsertedCell=cellWrapInsDoc.Descendants(w+"sdt").Descendants(w+"tc").Single(tc=>tc.Value.Contains("SDT_CELL_NEW"));
Check(sdtInsertedCell.Element(w+"tcPr")?.Element(w+"cellIns") is not null,"SDT wrapped inserted cell lacks tcPr/cellIns");
using(var wd=WordprocessingDocument.Open(cellWrapInsO,false)){var errors=new OpenXmlValidator().Validate(wd.MainDocumentPart!.Document).ToList();Check(errors.Count==0,"wrapped cell insertion OpenXML invalid: "+string.Join(" | ",errors.Take(5).Select(e=>e.Description)));}
var cellWrapDelA=Path.Combine(dir,"wrappedCellDelA.docx");var cellWrapDelB=Path.Combine(dir,"wrappedCellDelB.docx");var cellWrapDelO=Path.Combine(dir,"wrappedCellDelOut.docx");
Make(cellWrapDelA,Tbl1(RowCells1(Cell1("KEEP")+CustomCell1("CUSTOM_CELL_OLD"))));Make(cellWrapDelB,Tbl1(RowCells1(Cell1("KEEP"))));
await eng.ExportWordAsync(cellWrapDelA,cellWrapDelB,cellWrapDelO,"T",true);var cellWrapDelDoc=Doc(cellWrapDelO);
var deletedWrappedCell=cellWrapDelDoc.Descendants(w+"tc").SingleOrDefault(tc=>tc.Value.Contains("CUSTOM_CELL_OLD"));
Check(deletedWrappedCell?.Element(w+"tcPr")?.Element(w+"cellDel") is not null,"customXml wrapped deleted cell was not reconstructed with cellDel");
using(var wd=WordprocessingDocument.Open(cellWrapDelO,false)){var errors=new OpenXmlValidator().Validate(wd.MainDocumentPart!.Document).ToList();Check(errors.Count==0,"wrapped cell deletion OpenXML invalid: "+string.Join(" | ",errors.Take(5).Select(e=>e.Description)));}
Console.WriteLine("PASS WRAPPED TABLE CELL REVISION EXPORT");


// 36. Deleted table rows must be positioned by matched surviving row lineage, not raw A row ordinals.
var rowPosA=Path.Combine(dir,"rowPosA.docx");var rowPosB=Path.Combine(dir,"rowPosB.docx");var rowPosO=Path.Combine(dir,"rowPosOut.docx");
Make(rowPosA,Tbl1(Row1("KEEP_1")+Row1("DELETE_MIDDLE")+Row1("KEEP_3")));
Make(rowPosB,Tbl1(Row1("NEW_HEAD")+Row1("KEEP_1")+Row1("KEEP_3")));
await eng.ExportWordAsync(rowPosA,rowPosB,rowPosO,"T",true);var rowPosDoc=Doc(rowPosO);
var rowPosRows=rowPosDoc.Descendants(w+"tbl").First().Descendants(w+"tr").ToList();
var rowPosTexts=rowPosRows.Select(r=>string.Concat(r.Descendants(w+"t").Select(x=>x.Value))+string.Concat(r.Descendants(w+"delText").Select(x=>x.Value))).ToList();
Check(rowPosTexts.SequenceEqual(new[]{"NEW_HEAD","KEEP_1","DELETE_MIDDLE","KEEP_3"}),"deleted row raw-index placement regressed: "+string.Join(" | ",rowPosTexts));
Check(rowPosRows[2].Element(w+"trPr")?.Element(w+"del") is not null,"middle deleted row lacks trPr/del");
using(var wd=WordprocessingDocument.Open(rowPosO,false)){var errors=new OpenXmlValidator().Validate(wd.MainDocumentPart!.Document).ToList();Check(errors.Count==0,"row lineage placement OpenXML invalid: "+string.Join(" | ",errors.Take(5).Select(e=>e.Description)));}
Console.WriteLine("PASS DELETED ROW LINEAGE POSITION");

// 37. Deleted paragraphs inside a matched table cell must use matched paragraph neighbors, not raw paragraph ordinals.
string MultiPCell(params string[] values)=>"<w:tc>"+string.Concat(values.Select(P))+"</w:tc>";
var paraPosA=Path.Combine(dir,"paraPosA.docx");var paraPosB=Path.Combine(dir,"paraPosB.docx");var paraPosO=Path.Combine(dir,"paraPosOut.docx");
Make(paraPosA,Tbl1(RowCells1(MultiPCell("KEEP_P1","DELETE_P2","KEEP_P3"))));
Make(paraPosB,Tbl1(RowCells1(MultiPCell("NEW_P0","KEEP_P1","KEEP_P3"))));
await eng.ExportWordAsync(paraPosA,paraPosB,paraPosO,"T",true);var paraPosDoc=Doc(paraPosO);
var paraCell=paraPosDoc.Descendants(w+"tc").First();
var paraPosTexts=paraCell.Elements(w+"p").Select(p=>string.Concat(p.Descendants(w+"t").Select(x=>x.Value))+string.Concat(p.Descendants(w+"delText").Select(x=>x.Value))).ToList();
Check(paraPosTexts.SequenceEqual(new[]{"NEW_P0","KEEP_P1","DELETE_P2","KEEP_P3"}),"deleted paragraph raw-index placement regressed: "+string.Join(" | ",paraPosTexts));
using(var wd=WordprocessingDocument.Open(paraPosO,false)){var errors=new OpenXmlValidator().Validate(wd.MainDocumentPart!.Document).ToList();Check(errors.Count==0,"paragraph lineage placement OpenXML invalid: "+string.Join(" | ",errors.Take(5).Select(e=>e.Description)));}
Console.WriteLine("PASS DELETED TABLE PARAGRAPH LINEAGE POSITION");


// 38. Ancillary Word guards must detect non-text relationship payload changes (e.g. header logo/image).
var imgHdrA=Path.Combine(dir,"headerImageA.docx");var imgHdrB=Path.Combine(dir,"headerImageB.docx");var imgHdrO=Path.Combine(dir,"headerImageOut.docx");
Make(imgHdrA,P("Body"));Make(imgHdrB,P("Body"));
var headerWithImage="<w:hdr xmlns:w=\""+W+"\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:p><w:r><w:t>SAME HEADER</w:t></w:r><w:r><w:drawing><w:object r:id=\"rIdImg\"/></w:drawing></w:r></w:p></w:hdr>";
var headerRels="<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdImg\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/header-logo.bin\"/></Relationships>";
foreach(var path in new[]{imgHdrA,imgHdrB}){AddWordXml(path,"word/header1.xml",headerWithImage);AddWordXml(path,"word/_rels/header1.xml.rels",headerRels);}
AddWordBytes(imgHdrA,"word/media/header-logo.bin",Encoding.UTF8.GetBytes("IMAGE_A_BYTES"));
AddWordBytes(imgHdrB,"word/media/header-logo.bin",Encoding.UTF8.GetBytes("IMAGE_B_BYTES"));
var headerImageBlocked=false;try{await eng.ExportWordAsync(imgHdrA,imgHdrB,imgHdrO,"T",true);}catch(InvalidOperationException ex){headerImageBlocked=ex.Message.Contains("header")&&ex.Message.Contains("서로 다릅니다");}
Check(headerImageBlocked,"header image/media difference was silently omitted");
Console.WriteLine("PASS ANCILLARY RELATIONSHIP PAYLOAD GUARD");


// 39. Multiple similar additions from the same document and same gap must not overwrite each other.
var multiAddA=Path.Combine(dir,"multiAddA.txt");var multiAddB=Path.Combine(dir,"multiAddB.txt");
File.WriteAllText(multiAddA,"Article 1 (Stable)\nOne.\nArticle 4 (End)\nFour.",new UTF8Encoding(false));
File.WriteAllText(multiAddB,"Article 1 (Stable)\nOne.\nArticle 2 (Added)\nRepeated new clause.\nArticle 3 (Added)\nRepeated new clause.\nArticle 4 (End)\nFour.",new UTF8Encoding(false));
var multiAddCmp=await eng.CompareAsync(new[]{multiAddA,multiAddB},0,"legal",true,true);
var addedNumbers=multiAddCmp.Rows.Select(r=>r.Members[1]?.Number).Where(n=>n is "2" or "3").ToList();
Check(addedNumbers.Count==2&&addedNumbers.Contains("2")&&addedNumbers.Contains("3"),"same-gap additions were overwritten: "+string.Join(",",addedNumbers));
Console.WriteLine("PASS SAME-GAP MULTIPLE ADDITIONS");


// 40. Multiple fully changed nested sibling tables are ambiguous: never pair them by local ordinal alone.
string TwoNestedTables(string first,string second)=>"<w:tbl><w:tblPr/><w:tblGrid><w:gridCol/></w:tblGrid><w:tr><w:tc>"+P("OUTER_STABLE")+"<w:tbl><w:tblPr/><w:tblGrid><w:gridCol/></w:tblGrid><w:tr><w:tc>"+P(first)+"</w:tc></w:tr></w:tbl><w:tbl><w:tblPr/><w:tblGrid><w:gridCol/></w:tblGrid><w:tr><w:tc>"+P(second)+"</w:tc></w:tr></w:tbl>"+P("OUTER_END")+"</w:tc></w:tr></w:tbl>";
var ambNestedA=Path.Combine(dir,"ambNestedA.docx");var ambNestedB=Path.Combine(dir,"ambNestedB.docx");var ambNestedO=Path.Combine(dir,"ambNestedOut.docx");
Make(ambNestedA,TwoNestedTables("OLD_LEFT","OLD_RIGHT"));Make(ambNestedB,TwoNestedTables("NEW_LEFT","NEW_RIGHT"));
await eng.ExportWordAsync(ambNestedA,ambNestedB,ambNestedO,"T",true);var ambNestedDoc=Doc(ambNestedO);
var ambInnerTables=ambNestedDoc.Descendants(w+"tbl").Skip(1).ToList();
Check(ambInnerTables.Count==2,"ambiguous nested-table count changed");
Check(!ambInnerTables.Any(t=>t.Descendants(w+"t").Any(x=>x.Value.Contains("OLD_"))||t.Descendants(w+"delText").Any(x=>x.Value.Contains("OLD_"))),
    "ambiguous nested siblings were guessed by ordinal and polluted with old content");
Console.WriteLine("PASS AMBIGUOUS NESTED SIBLING SAFETY");


// 41. Ancillary guards must also catch non-text structural/layout changes inside the same part.
var hdrLayoutA=Path.Combine(dir,"headerLayoutA.docx");var hdrLayoutB=Path.Combine(dir,"headerLayoutB.docx");var hdrLayoutO=Path.Combine(dir,"headerLayoutOut.docx");
Make(hdrLayoutA,P("Body"));Make(hdrLayoutB,P("Body"));
AddWordXml(hdrLayoutA,"word/header1.xml","<w:hdr xmlns:w=\""+W+"\"><w:p><w:pPr><w:spacing w:before=\"0\"/></w:pPr><w:r><w:t>SAME HEADER</w:t></w:r></w:p></w:hdr>");
AddWordXml(hdrLayoutB,"word/header1.xml","<w:hdr xmlns:w=\""+W+"\"><w:p><w:pPr><w:spacing w:before=\"120\"/></w:pPr><w:r><w:t>SAME HEADER</w:t></w:r></w:p></w:hdr>");
var headerLayoutBlocked=false;try{await eng.ExportWordAsync(hdrLayoutA,hdrLayoutB,hdrLayoutO,"T",true);}catch(InvalidOperationException ex){headerLayoutBlocked=ex.Message.Contains("header")&&ex.Message.Contains("서로 다릅니다");}
Check(headerLayoutBlocked,"header non-text layout difference was silently omitted");
Console.WriteLine("PASS ANCILLARY STRUCTURE GUARD");


// 42. Python 5.19.4.4 parity: one-sided list expansion must not force unrelated plain-body pairing.
var matchPartsMethod=typeof(NativeComparisonEngine).GetMethod("MatchParts",BindingFlags.Static|BindingFlags.NonPublic)!;
var oneSideOld=parse.Invoke(null,new object[]{"Legacy authentication requirement applies only to registered operators."})!;
var oneSideNew=parse.Invoke(null,new object[]{"Unrelated marketing campaign preface for seasonal promotions.\n1. New benefit eligibility.\n2. New advertising schedule."})!;
var oneSideMatches=(System.Collections.IEnumerable)matchPartsMethod.Invoke(null,new[]{oneSideOld,oneSideNew})!;
var oneSideMatchCount=0;foreach(var _ in oneSideMatches)oneSideMatchCount++;
Check(oneSideMatchCount==0,"unrelated one-sided plain body was force-paired instead of remaining delete/add");
Console.WriteLine("PASS ONE-SIDED BODY PARITY");


// 43. A comparison result must become unusable for export when an input file changes in place.
var staleA=Path.Combine(dir,"staleA.txt");var staleB=Path.Combine(dir,"staleB.txt");var staleX=Path.Combine(dir,"stale.xlsx");
File.WriteAllText(staleA,"Alpha stable.",new UTF8Encoding(false));File.WriteAllText(staleB,"Alpha revised.",new UTF8Encoding(false));
var staleCmp=await eng.CompareAsync(new[]{staleA,staleB},0,"general",true,true);
File.AppendAllText(staleB," changed after comparison",new UTF8Encoding(false));
var staleBlocked=false;try{await eng.ExportExcelAsync(staleCmp,staleX);}catch(InvalidOperationException ex){staleBlocked=ex.Message.Contains("입력 파일이 변경");}
Check(staleBlocked,"stale comparison result exported after an input file changed in place");
Console.WriteLine("PASS INPUT FILE CHANGE INVALIDATION");


// 44. Repeated generic body wording must not override distinct article-title lineage.
var genericMoveA=Path.Combine(dir,"genericMoveA.docx");var genericMoveB=Path.Combine(dir,"genericMoveB.docx");
Make(genericMoveA,P("Article 1 (Start)")+P("Stable start.")+
    P("Article 2 (Payment Terms)")+P("The Company may provide the Service.")+
    P("Article 3 (Privacy Protection)")+P("The Company may provide the Service.")+
    P("Article 4 (End)")+P("Stable end."));
Make(genericMoveB,P("Article 1 (Start)")+P("Stable start.")+
    P("Article 2 (Privacy Protection)")+P("The Company may provide the Service.")+
    P("Article 3 (Payment Terms)")+P("The Company may provide the Service.")+
    P("Article 4 (End)")+P("Stable end."));
var genericMoveCmp=await eng.CompareAsync(new[]{genericMoveA,genericMoveB},0,"legal",true,true);
var paymentRow=genericMoveCmp.Rows.Single(r=>r.Members[0]?.Number=="2");
var privacyRow=genericMoveCmp.Rows.Single(r=>r.Members[0]?.Number=="3");
Check(paymentRow.Members[1]?.Number=="3"&&privacyRow.Members[1]?.Number=="2",
    "repeated generic body text overrode title lineage: "+paymentRow.Members[1]?.Number+" / "+privacyRow.Members[1]?.Number);
Console.WriteLine("PASS GENERIC WORDING LINEAGE GUARD");


// 45. Word export must never overwrite either input document, including original A.
var sameOutA=Path.Combine(dir,"sameOutputA.docx");var sameOutB=Path.Combine(dir,"sameOutputB.docx");
Make(sameOutA,P("ORIGINAL_A"));Make(sameOutB,P("REVISED_B"));
var sameOutBefore=File.ReadAllBytes(sameOutA);
var overwriteABlocked=false;try{await eng.ExportWordAsync(sameOutA,sameOutB,sameOutA,"T",true);}catch(InvalidOperationException ex){overwriteABlocked=ex.Message.Contains("원본")||ex.Message.Contains("입력");}
Check(overwriteABlocked,"Word export allowed outputPath to overwrite original A");
Check(File.ReadAllBytes(sameOutA).SequenceEqual(sameOutBefore),"original A was modified before same-path export was rejected");
Console.WriteLine("PASS WORD INPUT OVERWRITE GUARD");


// 49. Input invalidation must detect same-length content replacement even when mtime is preserved.
var stealthA=Path.Combine(dir,"stealthA.txt");var stealthB=Path.Combine(dir,"stealthB.txt");var stealthX=Path.Combine(dir,"stealth.xlsx");
File.WriteAllText(stealthA,"AAAA1111",new UTF8Encoding(false));File.WriteAllText(stealthB,"BBBB2222",new UTF8Encoding(false));
var stealthCmp=await eng.CompareAsync(new[]{stealthA,stealthB},0,"general",true,true);
var stealthStamp=File.GetLastWriteTimeUtc(stealthB);
File.WriteAllText(stealthB,"CCCC3333",new UTF8Encoding(false));File.SetLastWriteTimeUtc(stealthB,stealthStamp);
var stealthBlocked=false;try{await eng.ExportExcelAsync(stealthCmp,stealthX);}catch(InvalidOperationException ex){stealthBlocked=ex.Message.Contains("입력 파일이 변경");}
Check(stealthBlocked,"same-length same-mtime input mutation bypassed stale-result guard");
Console.WriteLine("PASS CONTENT HASH INPUT INVALIDATION");


// 50. Three-way additions in the same gap must not merge solely because their generic bodies match.
var gap3A=Path.Combine(dir,"gap3A.txt");var gap3B=Path.Combine(dir,"gap3B.txt");var gap3C=Path.Combine(dir,"gap3C.txt");
File.WriteAllText(gap3A,"Article 1 (Start)\nStable start.\nArticle 4 (End)\nStable end.",new UTF8Encoding(false));
File.WriteAllText(gap3B,"Article 1 (Start)\nStable start.\nArticle 2 (Payment Terms)\nThe Company may provide the Service.\nArticle 4 (End)\nStable end.",new UTF8Encoding(false));
File.WriteAllText(gap3C,"Article 1 (Start)\nStable start.\nArticle 2 (Privacy Protection)\nThe Company may provide the Service.\nArticle 4 (End)\nStable end.",new UTF8Encoding(false));
var gap3Cmp=await eng.CompareAsync(new[]{gap3A,gap3B,gap3C},0,"legal",true,true);
var paymentGapRow=gap3Cmp.Rows.Single(r=>r.Members[1]?.Title.Contains("Payment",StringComparison.OrdinalIgnoreCase)==true);
var privacyGapRow=gap3Cmp.Rows.Single(r=>r.Members[2]?.Title.Contains("Privacy",StringComparison.OrdinalIgnoreCase)==true);
Check(!ReferenceEquals(paymentGapRow,privacyGapRow)&&paymentGapRow.Members[2] is null&&privacyGapRow.Members[1] is null,
    "distinct same-gap B/C additions were merged by generic body wording");
Console.WriteLine("PASS THREE-WAY GENERIC ADDITION SEPARATION");


// 51. Cross-format Word export must not silently omit referenced DOCX ancillary content.
var mixTxt=Path.Combine(dir,"mixPlain.txt");var mixDoc=Path.Combine(dir,"mixHeader.docx");var mixOut=Path.Combine(dir,"mixOut.docx");
File.WriteAllText(mixTxt,"Body",new UTF8Encoding(false));Make(mixDoc,P("Body"));
AddWordXml(mixDoc,"word/document.xml","<w:document xmlns:w=\""+W+"\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body>"+P("Body")+"<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rIdHdr\"/></w:sectPr></w:body></w:document>");
AddWordXml(mixDoc,"word/_rels/document.xml.rels","<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdHdr\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/header\" Target=\"header1.xml\"/></Relationships>");
AddWordXml(mixDoc,"word/header1.xml","<w:hdr xmlns:w=\""+W+"\"><w:p><w:r><w:t>VISIBLE HEADER</w:t></w:r></w:p></w:hdr>");
var mixedBlocked=false;try{await eng.ExportWordAsync(mixTxt,mixDoc,mixOut,"T",true);}catch(InvalidOperationException ex){mixedBlocked=ex.Message.Contains("header/footer/footnote/endnote")||ex.Message.Contains("부속");}
Check(mixedBlocked,"TXT->DOCX export silently omitted referenced header ancestry");
var reverseMixedBlocked=false;try{await eng.ExportWordAsync(mixDoc,mixTxt,mixOut,"T",true);}catch(InvalidOperationException ex){reverseMixedBlocked=ex.Message.Contains("header/footer/footnote/endnote")||ex.Message.Contains("부속");}
Check(reverseMixedBlocked,"DOCX->TXT export silently omitted referenced header ancestry");
Console.WriteLine("PASS CROSS-FORMAT ANCILLARY GUARD");


// 52. Main-document relationship changes (hyperlink/image/OLE) must never pass as text-identical.
var linkA=Path.Combine(dir,"bodyLinkA.docx");var linkB=Path.Combine(dir,"bodyLinkB.docx");var linkO=Path.Combine(dir,"bodyLinkOut.docx");
Make(linkA,P("Body"));Make(linkB,P("Body"));
var linkDoc="<w:document xmlns:w=\""+W+"\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body><w:p><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>Same Link Text</w:t></w:r></w:hyperlink></w:p><w:sectPr/></w:body></w:document>";
AddWordXml(linkA,"word/document.xml",linkDoc);AddWordXml(linkB,"word/document.xml",linkDoc);
AddWordXml(linkA,"word/_rels/document.xml.rels","<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdLink\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"https://example.com/a\" TargetMode=\"External\"/></Relationships>");
AddWordXml(linkB,"word/_rels/document.xml.rels","<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdLink\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"https://example.com/b\" TargetMode=\"External\"/></Relationships>");
var bodyRelBlocked=false;try{await eng.ExportWordAsync(linkA,linkB,linkO,"T",true);}catch(InvalidOperationException ex){bodyRelBlocked=ex.Message.Contains("본문")&&ex.Message.Contains("관계");}
Check(bodyRelBlocked,"body hyperlink target change silently passed as text-identical");
Console.WriteLine("PASS MAIN-DOCUMENT RELATIONSHIP GUARD");


// 53. Untitled same-gap additions with different article numbers must not merge on generic body text alone.
var gapNumA=Path.Combine(dir,"gapNumA.txt");var gapNumB=Path.Combine(dir,"gapNumB.txt");var gapNumC=Path.Combine(dir,"gapNumC.txt");
File.WriteAllText(gapNumA,"Article 1 (Start)\nStable start.\nArticle 5 (End)\nStable end.",new UTF8Encoding(false));
File.WriteAllText(gapNumB,"Article 1 (Start)\nStable start.\nArticle 2\nThe Company may provide the Service.\nArticle 5 (End)\nStable end.",new UTF8Encoding(false));
File.WriteAllText(gapNumC,"Article 1 (Start)\nStable start.\nArticle 3\nThe Company may provide the Service.\nArticle 5 (End)\nStable end.",new UTF8Encoding(false));
var gapNumCmp=await eng.CompareAsync(new[]{gapNumA,gapNumB,gapNumC},0,"legal",true,true);
var gapBRow=gapNumCmp.Rows.Single(r=>r.Members[1]?.Number=="2");
var gapCRow=gapNumCmp.Rows.Single(r=>r.Members[2]?.Number=="3");
Check(!ReferenceEquals(gapBRow,gapCRow)&&gapBRow.Members[2] is null&&gapCRow.Members[1] is null,
    "different-number untitled additions merged on generic body alone");
Console.WriteLine("PASS THREE-WAY UNTITLED GENERIC SEPARATION");


// 54. Canceled Excel export must preserve an existing good output and clean temporary files.
var atomicX=Path.Combine(dir,"atomic.xlsx");var atomicXSentinel=Encoding.UTF8.GetBytes("PREVIOUS_GOOD_XLSX");
File.WriteAllBytes(atomicX,atomicXSentinel);
var atomicResult=new ComparisonResultVm{Names=new List<string>{"A","B"},BaseIndex=0,
    Rows=Enumerable.Range(0,200000).Select(i=>new ComparisonRowVm{Id=i+1,Members=new List<MemberVm?>{null,null}}).ToList()};
using(var atomicXCts=new CancellationTokenSource()){
    var atomicXTask=eng.ExportExcelAsync(atomicResult,atomicX,atomicXCts.Token);
    var pattern="."+Path.GetFileName(atomicX)+".*.tmp";
    for(var i=0;i<500&&!Directory.GetFiles(dir,pattern).Any()&&!atomicXTask.IsCompleted;i++)await Task.Delay(1);
    atomicXCts.Cancel();
    try{await atomicXTask;}catch(OperationCanceledException){}
}
Check(File.ReadAllBytes(atomicX).SequenceEqual(atomicXSentinel),"canceled Excel export damaged the previous output");
Check(!Directory.GetFiles(dir,"."+Path.GetFileName(atomicX)+".*.tmp").Any(),"Excel temp file leaked after cancellation");
Console.WriteLine("PASS ATOMIC EXCEL CANCELLATION");

// 55. Canceled Word export must preserve an existing good output and clean temporary files.
var atomicWA=Path.Combine(dir,"atomicWordA.docx");var atomicWB=Path.Combine(dir,"atomicWordB.docx");var atomicWO=Path.Combine(dir,"atomicWordOut.docx");
var manyA=string.Concat(Enumerable.Range(0,1200).Select(i=>P("OLD_"+i.ToString("D4"))));
var manyB=string.Concat(Enumerable.Range(0,1200).Select(i=>P("NEW_"+i.ToString("D4"))));
Make(atomicWA,manyA);Make(atomicWB,manyB);var atomicWSentinel=Encoding.UTF8.GetBytes("PREVIOUS_GOOD_WORD");File.WriteAllBytes(atomicWO,atomicWSentinel);
using(var atomicWCts=new CancellationTokenSource()){
    var atomicWTask=eng.ExportWordAsync(atomicWA,atomicWB,atomicWO,"T",true,atomicWCts.Token);
    var pattern="."+Path.GetFileName(atomicWO)+".*.tmp";
    for(var i=0;i<1000&&!Directory.GetFiles(dir,pattern).Any()&&!atomicWTask.IsCompleted;i++)await Task.Delay(1);
    atomicWCts.Cancel();
    try{await atomicWTask;}catch(OperationCanceledException){}
}
Check(File.ReadAllBytes(atomicWO).SequenceEqual(atomicWSentinel),"canceled Word export damaged the previous output");
Check(!Directory.GetFiles(dir,"."+Path.GetFileName(atomicWO)+".*.tmp").Any(),"Word temp file leaked after cancellation");
Console.WriteLine("PASS ATOMIC WORD CANCELLATION");


// 56. Moving the same footnote reference to another paragraph must be detected as ancillary-location change.
var fnMoveA=Path.Combine(dir,"footnoteMoveA.docx");var fnMoveB=Path.Combine(dir,"footnoteMoveB.docx");var fnMoveO=Path.Combine(dir,"footnoteMoveOut.docx");
Make(fnMoveA,P("First")+P("Second"));Make(fnMoveB,P("First")+P("Second"));
var fnDocA="<w:document xmlns:w=\""+W+"\"><w:body><w:p><w:r><w:t>First</w:t></w:r><w:r><w:footnoteReference w:id=\"2\"/></w:r></w:p>"+P("Second")+"<w:sectPr/></w:body></w:document>";
var fnDocB="<w:document xmlns:w=\""+W+"\"><w:body>"+P("First")+"<w:p><w:r><w:t>Second</w:t></w:r><w:r><w:footnoteReference w:id=\"2\"/></w:r></w:p><w:sectPr/></w:body></w:document>";
AddWordXml(fnMoveA,"word/document.xml",fnDocA);AddWordXml(fnMoveB,"word/document.xml",fnDocB);
var footnotes="<w:footnotes xmlns:w=\""+W+"\"><w:footnote w:id=\"2\">"+P("Same footnote")+"</w:footnote></w:footnotes>";
AddWordXml(fnMoveA,"word/footnotes.xml",footnotes);AddWordXml(fnMoveB,"word/footnotes.xml",footnotes);
var fnMoveBlocked=false;try{await eng.ExportWordAsync(fnMoveA,fnMoveB,fnMoveO,"T",true);}catch(InvalidOperationException ex){fnMoveBlocked=ex.Message.Contains("참조 위치")||ex.Message.Contains("footnote");}
Check(fnMoveBlocked,"moved footnote reference was treated as the same location");
Console.WriteLine("PASS FOOTNOTE REFERENCE LOCATION GUARD");


// 57. Excel export must never overwrite any compared source file.
var excelSourceA=Path.Combine(dir,"excelSourceA.txt");var excelSourceB=Path.Combine(dir,"excelSourceB.txt");
File.WriteAllText(excelSourceA,"Source A",new UTF8Encoding(false));File.WriteAllText(excelSourceB,"Source B",new UTF8Encoding(false));
var excelSourceCmp=await eng.CompareAsync(new[]{excelSourceA,excelSourceB},0,"general",true,true);
var excelSourceBefore=File.ReadAllBytes(excelSourceA);var excelOverwriteBlocked=false;
try{await eng.ExportExcelAsync(excelSourceCmp,excelSourceA);}catch(InvalidOperationException ex){excelOverwriteBlocked=ex.Message.Contains("원본")||ex.Message.Contains("입력");}
Check(excelOverwriteBlocked,"Excel export allowed overwriting a compared source");
Check(File.ReadAllBytes(excelSourceA).SequenceEqual(excelSourceBefore),"Excel overwrite guard modified source A");
Console.WriteLine("PASS EXCEL INPUT OVERWRITE GUARD");


// 58. Different-number same-gap additions need positive title evidence even if only one side has a title.
var gapHalfA=Path.Combine(dir,"gapHalfA.txt");var gapHalfB=Path.Combine(dir,"gapHalfB.txt");var gapHalfC=Path.Combine(dir,"gapHalfC.txt");
File.WriteAllText(gapHalfA,"Article 1 (Start)\nStable.\nArticle 5 (End)\nStable.",new UTF8Encoding(false));
File.WriteAllText(gapHalfB,"Article 1 (Start)\nStable.\nArticle 2 (Payment Terms)\nThe Company may provide the Service.\nArticle 5 (End)\nStable.",new UTF8Encoding(false));
File.WriteAllText(gapHalfC,"Article 1 (Start)\nStable.\nArticle 3\nThe Company may provide the Service.\nArticle 5 (End)\nStable.",new UTF8Encoding(false));
var gapHalfCmp=await eng.CompareAsync(new[]{gapHalfA,gapHalfB,gapHalfC},0,"legal",true,true);
var gapHalfBRow=gapHalfCmp.Rows.Single(r=>r.Members[1]?.Number=="2");
var gapHalfCRow=gapHalfCmp.Rows.Single(r=>r.Members[2]?.Number=="3");
Check(!ReferenceEquals(gapHalfBRow,gapHalfCRow)&&gapHalfBRow.Members[2] is null&&gapHalfCRow.Members[1] is null,
    "different-number addition with one missing title merged on body alone");
Console.WriteLine("PASS THREE-WAY PARTIAL-TITLE SEPARATION");


// 59. The same body relationship moved to another paragraph must be treated as an unsupported position change.
var relMoveA=Path.Combine(dir,"relMoveA.docx");var relMoveB=Path.Combine(dir,"relMoveB.docx");var relMoveO=Path.Combine(dir,"relMoveOut.docx");
Make(relMoveA,P("Alpha")+P("Alpha"));Make(relMoveB,P("Alpha")+P("Alpha"));
var relMoveDocA="<w:document xmlns:w=\""+W+"\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body><w:p><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>Alpha</w:t></w:r></w:hyperlink></w:p>"+P("Alpha")+"<w:sectPr/></w:body></w:document>";
var relMoveDocB="<w:document xmlns:w=\""+W+"\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body>"+P("Alpha")+"<w:p><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>Alpha</w:t></w:r></w:hyperlink></w:p><w:sectPr/></w:body></w:document>";
var sameLinkRels="<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdLink\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"https://example.com/same\" TargetMode=\"External\"/></Relationships>";
AddWordXml(relMoveA,"word/document.xml",relMoveDocA);AddWordXml(relMoveB,"word/document.xml",relMoveDocB);
AddWordXml(relMoveA,"word/_rels/document.xml.rels",sameLinkRels);AddWordXml(relMoveB,"word/_rels/document.xml.rels",sameLinkRels);
var relMoveBlocked=false;try{await eng.ExportWordAsync(relMoveA,relMoveB,relMoveO,"T",true);}catch(InvalidOperationException ex){relMoveBlocked=ex.Message.Contains("비텍스트")||ex.Message.Contains("관계");}
Check(relMoveBlocked,"body relationship moved between paragraphs without being detected");
Console.WriteLine("PASS MAIN-DOCUMENT RELATIONSHIP LOCATION GUARD");

// 60. Source mutation during Excel export must abort before replacing an existing output.
var raceXA=Path.Combine(dir,"raceExcelA.txt");var raceXB=Path.Combine(dir,"raceExcelB.txt");var raceXO=Path.Combine(dir,"raceExcelOut.xlsx");
File.WriteAllText(raceXA,"Race A",new UTF8Encoding(false));File.WriteAllText(raceXB,"Race B",new UTF8Encoding(false));
var raceXCmpBase=await eng.CompareAsync(new[]{raceXA,raceXB},0,"general",true,true);
var raceXCmp=new ComparisonResultVm{Names=raceXCmpBase.Names,BaseIndex=0,SourceFiles=raceXCmpBase.SourceFiles,
    Rows=Enumerable.Range(0,180000).Select(i=>new ComparisonRowVm{Id=i+1,Members=new List<MemberVm?>{null,null}}).ToList()};
var raceXSentinel=Encoding.UTF8.GetBytes("PREVIOUS_RACE_XLSX");File.WriteAllBytes(raceXO,raceXSentinel);
var raceXTask=eng.ExportExcelAsync(raceXCmp,raceXO);
var raceXPattern="."+Path.GetFileName(raceXO)+".*.tmp";
for(var i=0;i<1000&&!Directory.GetFiles(dir,raceXPattern).Any()&&!raceXTask.IsCompleted;i++)await Task.Delay(1);
File.WriteAllText(raceXB,"Race B changed during export",new UTF8Encoding(false));
var raceXBlocked=false;try{await raceXTask;}catch(InvalidOperationException ex){raceXBlocked=ex.Message.Contains("내보내기 중 입력 파일이 변경")||ex.Message.Contains("입력 파일이 변경");}
Check(raceXBlocked,"Excel export committed after a compared source changed mid-export");
Check(File.ReadAllBytes(raceXO).SequenceEqual(raceXSentinel),"Excel race guard replaced the previous output");
Console.WriteLine("PASS EXCEL MID-EXPORT SOURCE RACE GUARD");

// 61. Source mutation during Word export must abort before replacing an existing output.
var raceWA=Path.Combine(dir,"raceWordA.docx");var raceWB=Path.Combine(dir,"raceWordB.docx");var raceWO=Path.Combine(dir,"raceWordOut.docx");
var raceManyA=string.Concat(Enumerable.Range(0,1400).Select(i=>P("RACE_OLD_"+i.ToString("D4"))));
var raceManyB=string.Concat(Enumerable.Range(0,1400).Select(i=>P("RACE_NEW_"+i.ToString("D4"))));
Make(raceWA,raceManyA);Make(raceWB,raceManyB);
var raceWCmp=await eng.CompareAsync(new[]{raceWA,raceWB},0,"general",true,true);
var raceWSentinel=Encoding.UTF8.GetBytes("PREVIOUS_RACE_WORD");File.WriteAllBytes(raceWO,raceWSentinel);
var raceWTask=eng.ExportWordAsync(raceWA,raceWB,raceWO,"T",true,default,raceWCmp,0,1);
var raceWPattern="."+Path.GetFileName(raceWO)+".*.tmp";
for(var i=0;i<1500&&!Directory.GetFiles(dir,raceWPattern).Any()&&!raceWTask.IsCompleted;i++)await Task.Delay(1);
// The Word exporter may briefly hold an exclusive input read handle on
// Windows. Retry the *mutation attempt* while export is still running,
// rather than failing the test with an unrelated sharing violation.
var raceWMutated=false;
for(var attempt=0;attempt<2000 && !raceWMutated && !raceWTask.IsCompleted;attempt++)
{
    try
    {
        using var raceFs=new FileStream(raceWB,FileMode.Open,FileAccess.ReadWrite,
            FileShare.ReadWrite|FileShare.Delete);
        raceFs.Position=Math.Max(0,raceFs.Length-8);
        raceFs.WriteByte(0x20);
        raceWMutated=true;
    }
    catch(IOException)
    {
        await Task.Delay(2);
    }
}
Check(raceWMutated,"Word input mutation never acquired a write window during active export");
var raceWBlocked=false;try{await raceWTask;}catch(InvalidOperationException ex){raceWBlocked=ex.Message.Contains("내보내기 중 입력 파일이 변경")||ex.Message.Contains("입력 파일이 변경");}catch(InvalidDataException){raceWBlocked=true;}
Check(raceWBlocked,"Word export committed after a compared source changed mid-export");
Check(File.ReadAllBytes(raceWO).SequenceEqual(raceWSentinel),"Word race guard replaced the previous output");
Console.WriteLine("PASS WORD MID-EXPORT SOURCE RACE GUARD");


// 62. Inserting an unrelated paragraph before an unchanged hyperlink must not trigger a false relationship-location block.
var relShiftA=Path.Combine(dir,"relShiftA.docx");var relShiftB=Path.Combine(dir,"relShiftB.docx");var relShiftO=Path.Combine(dir,"relShiftOut.docx");
Make(relShiftA,P("Alpha")+P("Beta"));Make(relShiftB,P("Intro")+P("Alpha")+P("Beta"));
var relShiftDocA="<w:document xmlns:w=\""+W+"\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body><w:p><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>Alpha</w:t></w:r></w:hyperlink></w:p>"+P("Beta")+"<w:sectPr/></w:body></w:document>";
var relShiftDocB="<w:document xmlns:w=\""+W+"\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body>"+P("Intro")+"<w:p><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>Alpha</w:t></w:r></w:hyperlink></w:p>"+P("Beta")+"<w:sectPr/></w:body></w:document>";
AddWordXml(relShiftA,"word/document.xml",relShiftDocA);AddWordXml(relShiftB,"word/document.xml",relShiftDocB);
AddWordXml(relShiftA,"word/_rels/document.xml.rels",sameLinkRels);AddWordXml(relShiftB,"word/_rels/document.xml.rels",sameLinkRels);
await eng.ExportWordAsync(relShiftA,relShiftB,relShiftO,"T",true);
Check(File.Exists(relShiftO),"unchanged hyperlink was falsely blocked after an unrelated paragraph insertion");
Console.WriteLine("PASS RELATIONSHIP LOGICAL-LOCATION SHIFT");

// 63. Inserting an unrelated paragraph before an unchanged footnote owner must not look like a footnote move.
var fnShiftA=Path.Combine(dir,"fnShiftA.docx");var fnShiftB=Path.Combine(dir,"fnShiftB.docx");var fnShiftO=Path.Combine(dir,"fnShiftOut.docx");
Make(fnShiftA,P("Alpha")+P("Beta"));Make(fnShiftB,P("Intro")+P("Alpha")+P("Beta"));
var fnShiftDocA="<w:document xmlns:w=\""+W+"\"><w:body><w:p><w:r><w:t>Alpha</w:t></w:r><w:r><w:footnoteReference w:id=\"2\"/></w:r></w:p>"+P("Beta")+"<w:sectPr/></w:body></w:document>";
var fnShiftDocB="<w:document xmlns:w=\""+W+"\"><w:body>"+P("Intro")+"<w:p><w:r><w:t>Alpha</w:t></w:r><w:r><w:footnoteReference w:id=\"2\"/></w:r></w:p>"+P("Beta")+"<w:sectPr/></w:body></w:document>";
AddWordXml(fnShiftA,"word/document.xml",fnShiftDocA);AddWordXml(fnShiftB,"word/document.xml",fnShiftDocB);
AddWordXml(fnShiftA,"word/footnotes.xml",footnotes);AddWordXml(fnShiftB,"word/footnotes.xml",footnotes);
await eng.ExportWordAsync(fnShiftA,fnShiftB,fnShiftO,"T",true);
Check(File.Exists(fnShiftO),"unchanged footnote owner was falsely treated as moved after paragraph insertion");
Console.WriteLine("PASS FOOTNOTE LOGICAL-LOCATION SHIFT");


// 64. Same hyperlink relationship with edited display text must export normally.
var linkTextA=Path.Combine(dir,"linkTextA.docx");var linkTextB=Path.Combine(dir,"linkTextB.docx");var linkTextO=Path.Combine(dir,"linkTextOut.docx");
Make(linkTextA,P("Alpha"));Make(linkTextB,P("Alpha changed"));
var linkTextDocA="<w:document xmlns:w=\""+W+"\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body><w:p><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>Alpha</w:t></w:r></w:hyperlink></w:p><w:sectPr/></w:body></w:document>";
var linkTextDocB="<w:document xmlns:w=\""+W+"\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body><w:p><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>Alpha changed</w:t></w:r></w:hyperlink></w:p><w:sectPr/></w:body></w:document>";
AddWordXml(linkTextA,"word/document.xml",linkTextDocA);AddWordXml(linkTextB,"word/document.xml",linkTextDocB);
AddWordXml(linkTextA,"word/_rels/document.xml.rels",sameLinkRels);AddWordXml(linkTextB,"word/_rels/document.xml.rels",sameLinkRels);
await eng.ExportWordAsync(linkTextA,linkTextB,linkTextO,"T",true);
var linkTextOut=Doc(linkTextO);
Check(linkTextOut.Descendants(w+"hyperlink").Any(),"hyperlink lost after display-text edit");
Check(linkTextOut.Descendants().Any(x=>x.Name==w+"ins"||x.Name==w+"del"),"hyperlink display-text edit was not tracked");
Console.WriteLine("PASS HYPERLINK DISPLAY-TEXT EDIT");

// 65. Same URL reassigned to different visible text in the same paragraph must be blocked.
var linkAssocA=Path.Combine(dir,"linkAssocA.docx");var linkAssocB=Path.Combine(dir,"linkAssocB.docx");var linkAssocO=Path.Combine(dir,"linkAssocOut.docx");
Make(linkAssocA,P("Alpha Beta"));Make(linkAssocB,P("Alpha Beta"));
var linkAssocDocA="<w:document xmlns:w=\""+W+"\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body><w:p><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>Alpha</w:t></w:r></w:hyperlink><w:r><w:t xml:space=\"preserve\"> Beta</w:t></w:r></w:p><w:sectPr/></w:body></w:document>";
var linkAssocDocB="<w:document xmlns:w=\""+W+"\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body><w:p><w:r><w:t xml:space=\"preserve\">Alpha </w:t></w:r><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>Beta</w:t></w:r></w:hyperlink></w:p><w:sectPr/></w:body></w:document>";
AddWordXml(linkAssocA,"word/document.xml",linkAssocDocA);AddWordXml(linkAssocB,"word/document.xml",linkAssocDocB);
AddWordXml(linkAssocA,"word/_rels/document.xml.rels",sameLinkRels);AddWordXml(linkAssocB,"word/_rels/document.xml.rels",sameLinkRels);
var linkAssocBlocked=false;try{await eng.ExportWordAsync(linkAssocA,linkAssocB,linkAssocO,"T",true);}catch(InvalidOperationException ex){linkAssocBlocked=ex.Message.Contains("비텍스트")||ex.Message.Contains("관계");}
Check(linkAssocBlocked,"same hyperlink target moved to different visible text without being blocked");
Console.WriteLine("PASS HYPERLINK ASSOCIATION MOVE GUARD");

// 66. Distinct titled articles with similar generic wording must follow title lineage, not same-number proximity.
var titleSwapA=Path.Combine(dir,"titleSwapA.txt");var titleSwapB=Path.Combine(dir,"titleSwapB.txt");
File.WriteAllText(titleSwapA,"Article 2 (Payment Terms)\nThe Company may provide the Service to registered members.\nArticle 3 (Privacy Protection)\nThe Company may provide the Service to registered users.",new UTF8Encoding(false));
File.WriteAllText(titleSwapB,"Article 2 (Privacy Protection)\nThe Company may provide the Service to registered users.\nArticle 3 (Payment Terms)\nThe Company may provide the Service to registered members.",new UTF8Encoding(false));
var titleSwapCmp=await eng.CompareAsync(new[]{titleSwapA,titleSwapB},0,"legal",true,true);
var titleSwapPaymentRow=titleSwapCmp.Rows.Single(r=>r.Members[0]?.Title=="Payment Terms");
var titleSwapPrivacyRow=titleSwapCmp.Rows.Single(r=>r.Members[0]?.Title=="Privacy Protection");
Check(titleSwapPaymentRow.Members[1]?.Title=="Payment Terms"&&titleSwapPaymentRow.Members[1]?.Number=="3","Payment Terms followed generic body/number instead of title lineage");
Check(titleSwapPrivacyRow.Members[1]?.Title=="Privacy Protection"&&titleSwapPrivacyRow.Members[1]?.Number=="2","Privacy Protection followed generic body/number instead of title lineage");
Console.WriteLine("PASS GENERIC-BODY TITLE-LINEAGE SWAP");

// 67. w:noBreakHyphen is visible text and must compare like a normal hyphen.
var nbhA=Path.Combine(dir,"noBreakHyphenA.docx");var nbhB=Path.Combine(dir,"noBreakHyphenB.txt");
Make(nbhA,"<w:p><w:r><w:t>Alpha</w:t><w:noBreakHyphen/><w:t>Beta</w:t></w:r></w:p>");
File.WriteAllText(nbhB,"Alpha-Beta",new UTF8Encoding(false));
var nbhCmp=await eng.CompareAsync(new[]{nbhA,nbhB},0,"general",true,true);
Check(!nbhCmp.Rows.Any(r=>r.Changed),"w:noBreakHyphen disappeared and created a fake change");
Console.WriteLine("PASS NO-BREAK HYPHEN TEXT");

// 68. Word tracked export must preserve noBreakHyphen and keep offsets aligned around it.
var nbhExpA=Path.Combine(dir,"noBreakExportA.docx");var nbhExpB=Path.Combine(dir,"noBreakExportB.docx");var nbhExpO=Path.Combine(dir,"noBreakExportOut.docx");
Make(nbhExpA,"<w:p><w:r><w:t>Alpha</w:t><w:noBreakHyphen/><w:t>Beta</w:t></w:r></w:p>");
Make(nbhExpB,"<w:p><w:r><w:t>Alpha</w:t><w:noBreakHyphen/><w:t>Gamma</w:t></w:r></w:p>");
await eng.ExportWordAsync(nbhExpA,nbhExpB,nbhExpO,"T",true);
var nbhExpDoc=Doc(nbhExpO);
Check(nbhExpDoc.Descendants(w+"noBreakHyphen").Any(),"noBreakHyphen was lost during tracked export");
Check(nbhExpDoc.Descendants().Any(x=>x.Name==w+"ins")&&nbhExpDoc.Descendants().Any(x=>x.Name==w+"del"),"text change around noBreakHyphen was not tracked correctly");
Console.WriteLine("PASS NO-BREAK HYPHEN TRACKED EXPORT");

// 69. Inserting ordinary text before the same hyperlink inside a paragraph must not be mistaken for relationship reassignment.
var linkInlineA=Path.Combine(dir,"linkInlineA.docx");var linkInlineB=Path.Combine(dir,"linkInlineB.docx");var linkInlineO=Path.Combine(dir,"linkInlineOut.docx");
Make(linkInlineA,P("Alpha"));Make(linkInlineB,P("Intro Alpha"));
var linkInlineDocA="<w:document xmlns:w=\""+W+"\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body><w:p><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>Alpha</w:t></w:r></w:hyperlink></w:p><w:sectPr/></w:body></w:document>";
var linkInlineDocB="<w:document xmlns:w=\""+W+"\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body><w:p><w:r><w:t xml:space=\"preserve\">Intro </w:t></w:r><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>Alpha</w:t></w:r></w:hyperlink></w:p><w:sectPr/></w:body></w:document>";
AddWordXml(linkInlineA,"word/document.xml",linkInlineDocA);AddWordXml(linkInlineB,"word/document.xml",linkInlineDocB);
AddWordXml(linkInlineA,"word/_rels/document.xml.rels",sameLinkRels);AddWordXml(linkInlineB,"word/_rels/document.xml.rels",sameLinkRels);
await eng.ExportWordAsync(linkInlineA,linkInlineB,linkInlineO,"T",true);
Check(File.Exists(linkInlineO),"ordinary text insertion before unchanged hyperlink was falsely blocked");
Console.WriteLine("PASS HYPERLINK INLINE-TEXT SHIFT");


// 70. Inserting ordinary paragraphs after the unchanged hyperlink owner must not create a false location change.
var linkAfterA=Path.Combine(dir,"linkAfterA.docx");var linkAfterB=Path.Combine(dir,"linkAfterB.docx");var linkAfterO=Path.Combine(dir,"linkAfterOut.docx");
Make(linkAfterA,P("Alpha")+P("Stable tail"));Make(linkAfterB,P("Alpha")+P("Inserted one")+P("Inserted two")+P("Stable tail"));
var linkAfterDocA="<w:document xmlns:w=\""+W+"\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body><w:p><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>Alpha</w:t></w:r></w:hyperlink></w:p>"+P("Stable tail")+"<w:sectPr/></w:body></w:document>";
var linkAfterDocB="<w:document xmlns:w=\""+W+"\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body><w:p><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>Alpha</w:t></w:r></w:hyperlink></w:p>"+P("Inserted one")+P("Inserted two")+P("Stable tail")+"<w:sectPr/></w:body></w:document>";
AddWordXml(linkAfterA,"word/document.xml",linkAfterDocA);AddWordXml(linkAfterB,"word/document.xml",linkAfterDocB);
AddWordXml(linkAfterA,"word/_rels/document.xml.rels",sameLinkRels);AddWordXml(linkAfterB,"word/_rels/document.xml.rels",sameLinkRels);
await eng.ExportWordAsync(linkAfterA,linkAfterB,linkAfterO,"T",true);
Check(File.Exists(linkAfterO),"paragraph insertion after unchanged hyperlink was falsely blocked");
Console.WriteLine("PASS HYPERLINK CONTEXT-WINDOW SHIFT");

// 71. Unique titled articles must follow title lineage across a full reorder even with highly similar boilerplate bodies.
var propA=Path.Combine(dir,"propertyLineageA.txt");var propB=Path.Combine(dir,"propertyLineageB.txt");
var propTitles=new[]{"Payment Terms","Privacy Protection","Service Suspension","Intellectual Property"};
var propBodies=new[]{"The Company may provide the Service to registered members.","The Company may provide the Service to registered users.","The Company may suspend the Service for registered members.","The Company may protect the Service for registered members."};
File.WriteAllText(propA,string.Join("\n",Enumerable.Range(0,4).SelectMany(i=>new[]{"Article "+(i+1)+" ("+propTitles[i]+")",propBodies[i]})),new UTF8Encoding(false));
var propOrder=new[]{2,0,3,1};
File.WriteAllText(propB,string.Join("\n",propOrder.SelectMany((src,i)=>new[]{"Article "+(i+1)+" ("+propTitles[src]+")",propBodies[src]})),new UTF8Encoding(false));
var propCmp=await eng.CompareAsync(new[]{propA,propB},0,"legal",true,true);
for(var i=0;i<propTitles.Length;i++){
    var row=propCmp.Rows.Single(r=>r.Members[0]?.Title==propTitles[i]);
    Check(row.Members[1]?.Title==propTitles[i],"title-lineage property failed for "+propTitles[i]+" -> "+row.Members[1]?.Title);
}
Console.WriteLine("PASS TITLE-LINEAGE REORDER PROPERTY");


// 72. Moving an unchanged field instruction to another paragraph must remain blocked as unsupported.
var fieldMoveA=Path.Combine(dir,"fieldMoveA.docx");var fieldMoveB=Path.Combine(dir,"fieldMoveB.docx");var fieldMoveO=Path.Combine(dir,"fieldMoveOut.docx");
Make(fieldMoveA,"<w:p><w:r><w:instrText>PAGE</w:instrText></w:r><w:r><w:t>One</w:t></w:r></w:p>"+P("Two"));
Make(fieldMoveB,P("One")+"<w:p><w:r><w:instrText>PAGE</w:instrText></w:r><w:r><w:t>Two</w:t></w:r></w:p>");
var fieldMoveBlocked=false;try{await eng.ExportWordAsync(fieldMoveA,fieldMoveB,fieldMoveO,"T",true);}catch(InvalidOperationException ex){fieldMoveBlocked=ex.Message.Contains("필드")||ex.Message.Contains("비텍스트");}
Check(fieldMoveBlocked,"field instruction moved between paragraphs without being blocked");
Console.WriteLine("PASS FIELD LOCATION GUARD");

// 73. An unchanged field may shift by paragraph insertions elsewhere without a false block.
var fieldShiftA=Path.Combine(dir,"fieldShiftA.docx");var fieldShiftB=Path.Combine(dir,"fieldShiftB.docx");var fieldShiftO=Path.Combine(dir,"fieldShiftOut.docx");
Make(fieldShiftA,"<w:p><w:r><w:instrText>PAGE</w:instrText></w:r><w:r><w:t>One</w:t></w:r></w:p>"+P("Stable tail"));
Make(fieldShiftB,P("Intro")+"<w:p><w:r><w:instrText>PAGE</w:instrText></w:r><w:r><w:t>One</w:t></w:r></w:p>"+P("Stable tail"));
await eng.ExportWordAsync(fieldShiftA,fieldShiftB,fieldShiftO,"T",true);
Check(File.Exists(fieldShiftO),"unchanged field was falsely blocked after paragraph insertion");
Console.WriteLine("PASS FIELD LOGICAL-LOCATION SHIFT");


// 74. Moving the same image relationship to a different paragraph must be blocked.
var imageMoveA=Path.Combine(dir,"imageMoveA.docx");var imageMoveB=Path.Combine(dir,"imageMoveB.docx");var imageMoveO=Path.Combine(dir,"imageMoveOut.docx");
Make(imageMoveA,P("Stable one")+"<w:p><w:r><w:t>Two</w:t></w:r><w:r><w:drawing xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:id=\"rIdImage\"/></w:r></w:p>"+P("Three")+P("Stable four"));
Make(imageMoveB,P("Stable one")+P("Two")+"<w:p><w:r><w:t>Three</w:t></w:r><w:r><w:drawing xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:id=\"rIdImage\"/></w:r></w:p>"+P("Stable four"));
var imageRels="<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdImage\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/image1.bin\"/></Relationships>";
AddWordXml(imageMoveA,"word/_rels/document.xml.rels",imageRels);AddWordXml(imageMoveB,"word/_rels/document.xml.rels",imageRels);
AddWordBytes(imageMoveA,"word/media/image1.bin",new byte[]{1,2,3,4});AddWordBytes(imageMoveB,"word/media/image1.bin",new byte[]{1,2,3,4});
var imageMoveBlocked=false;try{await eng.ExportWordAsync(imageMoveA,imageMoveB,imageMoveO,"T",true);}catch(InvalidOperationException ex){imageMoveBlocked=ex.Message.Contains("비텍스트")||ex.Message.Contains("관계");}
Check(imageMoveBlocked,"image relationship moved between paragraphs without being blocked");
Console.WriteLine("PASS IMAGE RELATIONSHIP LOCATION GUARD");

// 75. Moving an unchanged field between nearby paragraphs in a longer document must still be blocked.
var fieldNearA=Path.Combine(dir,"fieldNearA.docx");var fieldNearB=Path.Combine(dir,"fieldNearB.docx");var fieldNearO=Path.Combine(dir,"fieldNearOut.docx");
Make(fieldNearA,P("Stable one")+"<w:p><w:r><w:instrText>PAGE</w:instrText></w:r><w:r><w:t>Two</w:t></w:r></w:p>"+P("Three")+P("Stable four"));
Make(fieldNearB,P("Stable one")+P("Two")+"<w:p><w:r><w:instrText>PAGE</w:instrText></w:r><w:r><w:t>Three</w:t></w:r></w:p>"+P("Stable four"));
var fieldNearBlocked=false;try{await eng.ExportWordAsync(fieldNearA,fieldNearB,fieldNearO,"T",true);}catch(InvalidOperationException ex){fieldNearBlocked=ex.Message.Contains("필드")||ex.Message.Contains("비텍스트");}
Check(fieldNearBlocked,"field moved to nearby paragraph despite overlapping context");
Console.WriteLine("PASS FIELD NEARBY-MOVE GUARD");


// 76. A low-information exact title alone must not force lineage across unrelated bodies.
var genericTitleA=Path.Combine(dir,"genericTitleA.txt");var genericTitleB=Path.Combine(dir,"genericTitleB.txt");
File.WriteAllText(genericTitleA,
    "Article 1 (Start)\nStable start.\nArticle 2 (General)\nOld licensing obligations apply only to legacy desktop software.\nArticle 3 (Payment Terms)\nPayment shall be made monthly.",
    new UTF8Encoding(false));
File.WriteAllText(genericTitleB,
    "Article 1 (Start)\nStable start.\nArticle 2 (Privacy Protection)\nPersonal information shall be protected.\nArticle 3 (Payment Terms)\nPayment shall be made monthly.\nArticle 4 (General)\nNew mobile advertising rules apply to anonymous analytics.",
    new UTF8Encoding(false));
var genericTitleCmp=await eng.CompareAsync(new[]{genericTitleA,genericTitleB},0,"legal",true,true);
var oldGeneral=genericTitleCmp.Rows.Single(r=>r.Members[0]?.Number=="2");
Check(oldGeneral.Members[1]?.Title!="General",
    "generic exact title alone forced unrelated Article 2 General -> Article 4 General lineage");
Console.WriteLine("PASS GENERIC TITLE ANCHOR SAFETY");

// 77. Inserting an identical ordinary paragraph before the same field owner must not create a false block.
var fieldDupA=Path.Combine(dir,"fieldDupA.docx");var fieldDupB=Path.Combine(dir,"fieldDupB.docx");var fieldDupO=Path.Combine(dir,"fieldDupOut.docx");
Make(fieldDupA,
    P("Repeat")+
    "<w:p><w:r><w:instrText>PAGE</w:instrText></w:r><w:r><w:t>Repeat</w:t></w:r></w:p>"+
    P("Stable tail"));
Make(fieldDupB,
    P("Repeat")+P("Repeat")+
    "<w:p><w:r><w:instrText>PAGE</w:instrText></w:r><w:r><w:t>Repeat</w:t></w:r></w:p>"+
    P("Stable tail"));
await eng.ExportWordAsync(fieldDupA,fieldDupB,fieldDupO,"T",true);
Check(File.Exists(fieldDupO),"identical paragraph insertion before unchanged field owner was falsely blocked");
Console.WriteLine("PASS FIELD DUPLICATE-PARAGRAPH SHIFT");


// 78. An image-only relationship moved between different empty-text paragraphs must be blocked.
var imageEmptyA=Path.Combine(dir,"imageEmptyA.docx");var imageEmptyB=Path.Combine(dir,"imageEmptyB.docx");var imageEmptyO=Path.Combine(dir,"imageEmptyOut.docx");
Make(imageEmptyA,
    P("Stable one")+
    "<w:p><w:r><w:drawing xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:id=\"rIdImage\"/></w:r></w:p>"+
    P("Stable two")+P("Stable three"));
Make(imageEmptyB,
    P("Stable one")+P("Stable two")+
    "<w:p><w:r><w:drawing xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:id=\"rIdImage\"/></w:r></w:p>"+
    P("Stable three"));
AddWordXml(imageEmptyA,"word/_rels/document.xml.rels",imageRels);AddWordXml(imageEmptyB,"word/_rels/document.xml.rels",imageRels);
AddWordBytes(imageEmptyA,"word/media/image1.bin",new byte[]{1,2,3,4});AddWordBytes(imageEmptyB,"word/media/image1.bin",new byte[]{1,2,3,4});
var imageEmptyBlocked=false;try{await eng.ExportWordAsync(imageEmptyA,imageEmptyB,imageEmptyO,"T",true);}catch(InvalidOperationException ex){imageEmptyBlocked=ex.Message.Contains("비텍스트")||ex.Message.Contains("관계");}
Check(imageEmptyBlocked,"image-only relationship moved between empty-text paragraphs without being blocked");
Console.WriteLine("PASS IMAGE-ONLY OWNER LOCATION GUARD");

// 79. An image-only owner may shift globally when unrelated paragraphs are inserted elsewhere.
var imageEmptyShiftA=Path.Combine(dir,"imageEmptyShiftA.docx");var imageEmptyShiftB=Path.Combine(dir,"imageEmptyShiftB.docx");var imageEmptyShiftO=Path.Combine(dir,"imageEmptyShiftOut.docx");
Make(imageEmptyShiftA,
    P("Stable one")+
    "<w:p><w:r><w:drawing xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:id=\"rIdImage\"/></w:r></w:p>"+
    P("Stable two"));
Make(imageEmptyShiftB,
    P("Intro")+P("Stable one")+
    "<w:p><w:r><w:drawing xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:id=\"rIdImage\"/></w:r></w:p>"+
    P("Stable two"));
AddWordXml(imageEmptyShiftA,"word/_rels/document.xml.rels",imageRels);AddWordXml(imageEmptyShiftB,"word/_rels/document.xml.rels",imageRels);
AddWordBytes(imageEmptyShiftA,"word/media/image1.bin",new byte[]{1,2,3,4});AddWordBytes(imageEmptyShiftB,"word/media/image1.bin",new byte[]{1,2,3,4});
await eng.ExportWordAsync(imageEmptyShiftA,imageEmptyShiftB,imageEmptyShiftO,"T",true);
Check(File.Exists(imageEmptyShiftO),"unchanged image-only owner was falsely blocked after unrelated paragraph insertion");
Console.WriteLine("PASS IMAGE-ONLY LOGICAL-LOCATION SHIFT");

// 80. Moving the same hyperlink from the first to the second identical text occurrence must be blocked.
var linkRepeatA=Path.Combine(dir,"linkRepeatA.docx");var linkRepeatB=Path.Combine(dir,"linkRepeatB.docx");var linkRepeatO=Path.Combine(dir,"linkRepeatOut.docx");
Make(linkRepeatA,P("Alpha Alpha"));Make(linkRepeatB,P("Alpha Alpha"));
var linkRepeatDocA="<w:document xmlns:w=\""+W+"\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body><w:p><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>Alpha</w:t></w:r></w:hyperlink><w:r><w:t xml:space=\"preserve\"> Alpha</w:t></w:r></w:p><w:sectPr/></w:body></w:document>";
var linkRepeatDocB="<w:document xmlns:w=\""+W+"\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body><w:p><w:r><w:t xml:space=\"preserve\">Alpha </w:t></w:r><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>Alpha</w:t></w:r></w:hyperlink></w:p><w:sectPr/></w:body></w:document>";
AddWordXml(linkRepeatA,"word/document.xml",linkRepeatDocA);AddWordXml(linkRepeatB,"word/document.xml",linkRepeatDocB);
AddWordXml(linkRepeatA,"word/_rels/document.xml.rels",sameLinkRels);AddWordXml(linkRepeatB,"word/_rels/document.xml.rels",sameLinkRels);
var linkRepeatBlocked=false;try{await eng.ExportWordAsync(linkRepeatA,linkRepeatB,linkRepeatO,"T",true);}catch(InvalidOperationException ex){linkRepeatBlocked=ex.Message.Contains("비텍스트")||ex.Message.Contains("관계");}
Check(linkRepeatBlocked,"hyperlink moved between identical visible text occurrences without being blocked");
Console.WriteLine("PASS HYPERLINK REPEATED-TEXT ASSOCIATION GUARD");

// 81. Different-number General additions in the same 3-way gap must not merge on title alone.
var genericAddA=Path.Combine(dir,"genericAddA.txt");var genericAddB=Path.Combine(dir,"genericAddB.txt");var genericAddC=Path.Combine(dir,"genericAddC.txt");
File.WriteAllText(genericAddA,"Article 1 (Start)\nStable.\nArticle 5 (End)\nStable.",new UTF8Encoding(false));
File.WriteAllText(genericAddB,"Article 1 (Start)\nStable.\nArticle 2 (General)\nLegacy desktop licensing obligations apply.\nArticle 5 (End)\nStable.",new UTF8Encoding(false));
File.WriteAllText(genericAddC,"Article 1 (Start)\nStable.\nArticle 3 (General)\nAnonymous mobile advertising analytics rules apply.\nArticle 5 (End)\nStable.",new UTF8Encoding(false));
var genericAddCmp=await eng.CompareAsync(new[]{genericAddA,genericAddB,genericAddC},0,"legal",true,true);
var genericAddBRow=genericAddCmp.Rows.Single(r=>r.Members[1]?.Number=="2");
var genericAddCRow=genericAddCmp.Rows.Single(r=>r.Members[2]?.Number=="3");
Check(!ReferenceEquals(genericAddBRow,genericAddCRow)&&genericAddBRow.Members[2] is null&&genericAddCRow.Members[1] is null,
    "different-number General additions merged on generic title alone");
Console.WriteLine("PASS THREE-WAY GENERIC-TITLE SEPARATION");


// 82. Repeated hyperlink text must not be reassigned while unrelated prefix text is also inserted.
var linkRepeatEditA=Path.Combine(dir,"linkRepeatEditA.docx");var linkRepeatEditB=Path.Combine(dir,"linkRepeatEditB.docx");var linkRepeatEditO=Path.Combine(dir,"linkRepeatEditOut.docx");
Make(linkRepeatEditA,P("Alpha Alpha"));Make(linkRepeatEditB,P("Intro Alpha Alpha"));
var linkRepeatEditDocA="<w:document xmlns:w=\""+W+"\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body><w:p><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>Alpha</w:t></w:r></w:hyperlink><w:r><w:t xml:space=\"preserve\"> Alpha</w:t></w:r></w:p><w:sectPr/></w:body></w:document>";
var linkRepeatEditDocB="<w:document xmlns:w=\""+W+"\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body><w:p><w:r><w:t xml:space=\"preserve\">Intro Alpha </w:t></w:r><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>Alpha</w:t></w:r></w:hyperlink></w:p><w:sectPr/></w:body></w:document>";
AddWordXml(linkRepeatEditA,"word/document.xml",linkRepeatEditDocA);AddWordXml(linkRepeatEditB,"word/document.xml",linkRepeatEditDocB);
AddWordXml(linkRepeatEditA,"word/_rels/document.xml.rels",sameLinkRels);AddWordXml(linkRepeatEditB,"word/_rels/document.xml.rels",sameLinkRels);
var linkRepeatEditBlocked=false;try{await eng.ExportWordAsync(linkRepeatEditA,linkRepeatEditB,linkRepeatEditO,"T",true);}catch(InvalidOperationException ex){linkRepeatEditBlocked=ex.Message.Contains("비텍스트")||ex.Message.Contains("관계");}
Check(linkRepeatEditBlocked,"hyperlink reassignment between repeated text was hidden by simultaneous prefix insertion");
Console.WriteLine("PASS HYPERLINK REPEATED-TEXT EDIT GUARD");

// 83. Ordinary visible text may be appended in the same field-owning paragraph without changing the field.
var fieldTextA=Path.Combine(dir,"fieldTextA.docx");var fieldTextB=Path.Combine(dir,"fieldTextB.docx");var fieldTextO=Path.Combine(dir,"fieldTextOut.docx");
Make(fieldTextA,"<w:p><w:r><w:instrText>PAGE</w:instrText></w:r><w:r><w:t>Page</w:t></w:r></w:p>");
Make(fieldTextB,"<w:p><w:r><w:instrText>PAGE</w:instrText></w:r><w:r><w:t>Page current</w:t></w:r></w:p>");
await eng.ExportWordAsync(fieldTextA,fieldTextB,fieldTextO,"T",true);
Check(File.Exists(fieldTextO),"ordinary text edit in unchanged field paragraph was falsely blocked");
Console.WriteLine("PASS FIELD OWNER TEXT EDIT");

// 84. Moving an image between two adjacent empty paragraphs with the same outer anchors must still be blocked.
var imageEmptySiblingA=Path.Combine(dir,"imageEmptySiblingA.docx");var imageEmptySiblingB=Path.Combine(dir,"imageEmptySiblingB.docx");var imageEmptySiblingO=Path.Combine(dir,"imageEmptySiblingOut.docx");
Make(imageEmptySiblingA,
    P("Stable one")+
    "<w:p><w:r><w:drawing xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:id=\"rIdImage\"/></w:r></w:p>"+
    "<w:p/>"+P("Stable two"));
Make(imageEmptySiblingB,
    P("Stable one")+"<w:p/>"+
    "<w:p><w:r><w:drawing xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:id=\"rIdImage\"/></w:r></w:p>"+
    P("Stable two"));
AddWordXml(imageEmptySiblingA,"word/_rels/document.xml.rels",imageRels);AddWordXml(imageEmptySiblingB,"word/_rels/document.xml.rels",imageRels);
AddWordBytes(imageEmptySiblingA,"word/media/image1.bin",new byte[]{1,2,3,4});AddWordBytes(imageEmptySiblingB,"word/media/image1.bin",new byte[]{1,2,3,4});
var imageEmptySiblingBlocked=false;try{await eng.ExportWordAsync(imageEmptySiblingA,imageEmptySiblingB,imageEmptySiblingO,"T",true);}catch(InvalidOperationException ex){imageEmptySiblingBlocked=ex.Message.Contains("비텍스트")||ex.Message.Contains("관계");}
Check(imageEmptySiblingBlocked,"image moved between adjacent empty paragraphs with identical outer anchors");
Console.WriteLine("PASS IMAGE EMPTY-SIBLING LOCATION GUARD");


// 85. Inserting an identical paragraph before the same footnote owner must not look like a note move.
var fnDupA=Path.Combine(dir,"fnDupA.docx");var fnDupB=Path.Combine(dir,"fnDupB.docx");var fnDupO=Path.Combine(dir,"fnDupOut.docx");
Make(fnDupA,P("Repeat")+P("Repeat")+P("Stable tail"));Make(fnDupB,P("Repeat")+P("Repeat")+P("Repeat")+P("Stable tail"));
var fnDupDocA="<w:document xmlns:w=\""+W+"\"><w:body>"+P("Repeat")+"<w:p><w:r><w:t>Repeat</w:t></w:r><w:r><w:footnoteReference w:id=\"2\"/></w:r></w:p>"+P("Stable tail")+"<w:sectPr/></w:body></w:document>";
var fnDupDocB="<w:document xmlns:w=\""+W+"\"><w:body>"+P("Repeat")+P("Repeat")+"<w:p><w:r><w:t>Repeat</w:t></w:r><w:r><w:footnoteReference w:id=\"2\"/></w:r></w:p>"+P("Stable tail")+"<w:sectPr/></w:body></w:document>";
AddWordXml(fnDupA,"word/document.xml",fnDupDocA);AddWordXml(fnDupB,"word/document.xml",fnDupDocB);
AddWordXml(fnDupA,"word/footnotes.xml",footnotes);AddWordXml(fnDupB,"word/footnotes.xml",footnotes);
await eng.ExportWordAsync(fnDupA,fnDupB,fnDupO,"T",true);
Check(File.Exists(fnDupO),"identical paragraph insertion before unchanged footnote owner was falsely blocked");
Console.WriteLine("PASS FOOTNOTE DUPLICATE-PARAGRAPH SHIFT");

// 86. Moving the same footnote between identical paragraphs must still be detected.
var fnRepeatA=Path.Combine(dir,"fnRepeatA.docx");var fnRepeatB=Path.Combine(dir,"fnRepeatB.docx");var fnRepeatO=Path.Combine(dir,"fnRepeatOut.docx");
Make(fnRepeatA,P("Repeat")+P("Repeat")+P("Stable tail"));Make(fnRepeatB,P("Repeat")+P("Repeat")+P("Stable tail"));
var fnRepeatDocA="<w:document xmlns:w=\""+W+"\"><w:body><w:p><w:r><w:t>Repeat</w:t></w:r><w:r><w:footnoteReference w:id=\"2\"/></w:r></w:p>"+P("Repeat")+P("Stable tail")+"<w:sectPr/></w:body></w:document>";
var fnRepeatDocB="<w:document xmlns:w=\""+W+"\"><w:body>"+P("Repeat")+"<w:p><w:r><w:t>Repeat</w:t></w:r><w:r><w:footnoteReference w:id=\"2\"/></w:r></w:p>"+P("Stable tail")+"<w:sectPr/></w:body></w:document>";
AddWordXml(fnRepeatA,"word/document.xml",fnRepeatDocA);AddWordXml(fnRepeatB,"word/document.xml",fnRepeatDocB);
AddWordXml(fnRepeatA,"word/footnotes.xml",footnotes);AddWordXml(fnRepeatB,"word/footnotes.xml",footnotes);
var fnRepeatBlocked=false;try{await eng.ExportWordAsync(fnRepeatA,fnRepeatB,fnRepeatO,"T",true);}catch(InvalidOperationException ex){fnRepeatBlocked=ex.Message.Contains("참조 위치")||ex.Message.Contains("footnote");}
Check(fnRepeatBlocked,"footnote moved between identical paragraphs without being detected");
Console.WriteLine("PASS FOOTNOTE REPEATED-OWNER LOCATION GUARD");


// 87. Moving the same footnote within one paragraph must be detected even when visible text is unchanged.
var fnInlineA=Path.Combine(dir,"fnInlineA.docx");var fnInlineB=Path.Combine(dir,"fnInlineB.docx");var fnInlineO=Path.Combine(dir,"fnInlineOut.docx");
Make(fnInlineA,P("Alpha Beta"));Make(fnInlineB,P("Alpha Beta"));
var fnInlineDocA="<w:document xmlns:w=\""+W+"\"><w:body><w:p><w:r><w:t xml:space=\"preserve\">Alpha </w:t></w:r><w:r><w:footnoteReference w:id=\"2\"/></w:r><w:r><w:t>Beta</w:t></w:r></w:p><w:sectPr/></w:body></w:document>";
var fnInlineDocB="<w:document xmlns:w=\""+W+"\"><w:body><w:p><w:r><w:t>Alpha Beta</w:t></w:r><w:r><w:footnoteReference w:id=\"2\"/></w:r></w:p><w:sectPr/></w:body></w:document>";
AddWordXml(fnInlineA,"word/document.xml",fnInlineDocA);AddWordXml(fnInlineB,"word/document.xml",fnInlineDocB);
AddWordXml(fnInlineA,"word/footnotes.xml",footnotes);AddWordXml(fnInlineB,"word/footnotes.xml",footnotes);
var fnInlineBlocked=false;try{await eng.ExportWordAsync(fnInlineA,fnInlineB,fnInlineO,"T",true);}catch(InvalidOperationException ex){fnInlineBlocked=ex.Message.Contains("참조 위치")||ex.Message.Contains("footnote");}
Check(fnInlineBlocked,"footnote moved within the same paragraph without being detected");
Console.WriteLine("PASS FOOTNOTE INTRA-PARAGRAPH LOCATION GUARD");

// 88. Inserting ordinary visible text before an unchanged field must be trackable, not rejected by raw XML ordinal changes.
var fieldPrefixA=Path.Combine(dir,"fieldPrefixA.docx");var fieldPrefixB=Path.Combine(dir,"fieldPrefixB.docx");var fieldPrefixO=Path.Combine(dir,"fieldPrefixOut.docx");
Make(fieldPrefixA,"<w:p><w:r><w:instrText>PAGE</w:instrText></w:r><w:r><w:t>Page</w:t></w:r></w:p>");
Make(fieldPrefixB,"<w:p><w:r><w:t xml:space=\"preserve\">Intro </w:t></w:r><w:r><w:instrText>PAGE</w:instrText></w:r><w:r><w:t>Page</w:t></w:r></w:p>");
await eng.ExportWordAsync(fieldPrefixA,fieldPrefixB,fieldPrefixO,"T",true);
Check(File.Exists(fieldPrefixO),"ordinary text insertion before unchanged field was falsely blocked");
Console.WriteLine("PASS FIELD PREFIX TEXT INSERTION");

// 89. Expanding the linked range while paragraph text stays identical is a relationship-association change and must be blocked.
var linkRangeA=Path.Combine(dir,"linkRangeA.docx");var linkRangeB=Path.Combine(dir,"linkRangeB.docx");var linkRangeO=Path.Combine(dir,"linkRangeOut.docx");
Make(linkRangeA,P("Alpha Beta"));Make(linkRangeB,P("Alpha Beta"));
var linkRangeDocA="<w:document xmlns:w=\""+W+"\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body><w:p><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>Alpha</w:t></w:r></w:hyperlink><w:r><w:t xml:space=\"preserve\"> Beta</w:t></w:r></w:p><w:sectPr/></w:body></w:document>";
var linkRangeDocB="<w:document xmlns:w=\""+W+"\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><w:body><w:p><w:hyperlink r:id=\"rIdLink\"><w:r><w:t>Alpha Beta</w:t></w:r></w:hyperlink></w:p><w:sectPr/></w:body></w:document>";
AddWordXml(linkRangeA,"word/document.xml",linkRangeDocA);AddWordXml(linkRangeB,"word/document.xml",linkRangeDocB);
AddWordXml(linkRangeA,"word/_rels/document.xml.rels",sameLinkRels);AddWordXml(linkRangeB,"word/_rels/document.xml.rels",sameLinkRels);
var linkRangeBlocked=false;try{await eng.ExportWordAsync(linkRangeA,linkRangeB,linkRangeO,"T",true);}catch(InvalidOperationException ex){linkRangeBlocked=ex.Message.Contains("비텍스트")||ex.Message.Contains("관계");}
Check(linkRangeBlocked,"hyperlink range expanded without being detected");
Console.WriteLine("PASS HYPERLINK RANGE-ASSOCIATION GUARD");


// 90. Text inserted before an unchanged footnote boundary is trackable and must not be falsely blocked.
var fnPrefixA=Path.Combine(dir,"fnPrefixA.docx");var fnPrefixB=Path.Combine(dir,"fnPrefixB.docx");var fnPrefixO=Path.Combine(dir,"fnPrefixOut.docx");
Make(fnPrefixA,P("Alpha Beta"));Make(fnPrefixB,P("Intro Alpha Beta"));
var fnPrefixDocA="<w:document xmlns:w=\""+W+"\"><w:body><w:p><w:r><w:t xml:space=\"preserve\">Alpha </w:t></w:r><w:r><w:footnoteReference w:id=\"2\"/></w:r><w:r><w:t>Beta</w:t></w:r></w:p><w:sectPr/></w:body></w:document>";
var fnPrefixDocB="<w:document xmlns:w=\""+W+"\"><w:body><w:p><w:r><w:t xml:space=\"preserve\">Intro Alpha </w:t></w:r><w:r><w:footnoteReference w:id=\"2\"/></w:r><w:r><w:t>Beta</w:t></w:r></w:p><w:sectPr/></w:body></w:document>";
AddWordXml(fnPrefixA,"word/document.xml",fnPrefixDocA);AddWordXml(fnPrefixB,"word/document.xml",fnPrefixDocB);
AddWordXml(fnPrefixA,"word/footnotes.xml",footnotes);AddWordXml(fnPrefixB,"word/footnotes.xml",footnotes);
await eng.ExportWordAsync(fnPrefixA,fnPrefixB,fnPrefixO,"T",true);
Check(File.Exists(fnPrefixO),"text insertion before unchanged footnote boundary was falsely blocked");
Console.WriteLine("PASS FOOTNOTE PREFIX TEXT INSERTION");

// 91. A simultaneous text insertion must not hide a real footnote move.
var fnMoveEditA=Path.Combine(dir,"fnMoveEditA.docx");var fnMoveEditB=Path.Combine(dir,"fnMoveEditB.docx");var fnMoveEditO=Path.Combine(dir,"fnMoveEditOut.docx");
Make(fnMoveEditA,P("Alpha Beta"));Make(fnMoveEditB,P("Intro Alpha Beta"));
var fnMoveEditDocA=fnPrefixDocA;
var fnMoveEditDocB="<w:document xmlns:w=\""+W+"\"><w:body><w:p><w:r><w:t>Intro Alpha Beta</w:t></w:r><w:r><w:footnoteReference w:id=\"2\"/></w:r></w:p><w:sectPr/></w:body></w:document>";
AddWordXml(fnMoveEditA,"word/document.xml",fnMoveEditDocA);AddWordXml(fnMoveEditB,"word/document.xml",fnMoveEditDocB);
AddWordXml(fnMoveEditA,"word/footnotes.xml",footnotes);AddWordXml(fnMoveEditB,"word/footnotes.xml",footnotes);
var fnMoveEditBlocked=false;try{await eng.ExportWordAsync(fnMoveEditA,fnMoveEditB,fnMoveEditO,"T",true);}catch(InvalidOperationException ex){fnMoveEditBlocked=ex.Message.Contains("참조 위치")||ex.Message.Contains("footnote");}
Check(fnMoveEditBlocked,"text insertion hid a simultaneous footnote move");
Console.WriteLine("PASS FOOTNOTE MOVE-WITH-EDIT GUARD");

// 92. A visible prefix insertion must not hide moving the same field to the end of the paragraph.
var fieldMoveEditA=Path.Combine(dir,"fieldMoveEditA.docx");var fieldMoveEditB=Path.Combine(dir,"fieldMoveEditB.docx");var fieldMoveEditO=Path.Combine(dir,"fieldMoveEditOut.docx");
Make(fieldMoveEditA,"<w:p><w:r><w:instrText>PAGE</w:instrText></w:r><w:r><w:t>Page</w:t></w:r></w:p>");
Make(fieldMoveEditB,"<w:p><w:r><w:t>Intro Page</w:t></w:r><w:r><w:instrText>PAGE</w:instrText></w:r></w:p>");
var fieldMoveEditBlocked=false;try{await eng.ExportWordAsync(fieldMoveEditA,fieldMoveEditB,fieldMoveEditO,"T",true);}catch(InvalidOperationException ex){fieldMoveEditBlocked=ex.Message.Contains("비텍스트")||ex.Message.Contains("필드");}
Check(fieldMoveEditBlocked,"text insertion hid a simultaneous field move");
Console.WriteLine("PASS FIELD MOVE-WITH-EDIT GUARD");


// 93. A Word paragraph flattened to single spaces must recover every consecutive (1)..(7) item boundary.
var flatSeven=(System.Collections.IEnumerable)parse.Invoke(null,new object[]{
    "Lead: (1) One (2) Two (3) Three (4) Four (5) Five (6) Six (7) Seven"
})!;
var flatSevenLabels=new List<string>();
foreach(var x in flatSeven)
    flatSevenLabels.Add((string)x!.GetType().GetProperty("Label")!.GetValue(x)!);
for(var n=1;n<=7;n++)
    Check(flatSevenLabels.Contains("("+n+")"),"flattened numeric item boundary missing: ("+n+") => "+string.Join(",",flatSevenLabels));
Console.WriteLine("PASS FLATTENED PAREN NUMBER SEVEN");


// 94. Word non-breaking spaces around flattened legal enumerators must behave like ordinary spaces.
var nbspSeven=(System.Collections.IEnumerable)parse.Invoke(null,new object[]{
    "Lead: (1) One (2) Two (3) Three (4) Four (5) Five (6) Six (7) Seven"
})!;
var nbspSevenLabels=new List<string>();
foreach(var x in nbspSeven)
    nbspSevenLabels.Add((string)x!.GetType().GetProperty("Label")!.GetValue(x)!);
for(var n=1;n<=7;n++)
    Check(nbspSevenLabels.Contains("("+n+")"),"NBSP flattened numeric item boundary missing: ("+n+") => "+string.Join(",",nbspSevenLabels));
Console.WriteLine("PASS NBSP FLATTENED PAREN NUMBER SEVEN");

// 95. Word export without a ComparisonResult must still reject a source mutation during export.
var rawRaceA=Path.Combine(dir,"rawRaceA.docx");var rawRaceB=Path.Combine(dir,"rawRaceB.docx");var rawRaceO=Path.Combine(dir,"rawRaceOut.docx");
var rawRaceOld=string.Concat(Enumerable.Range(0,120).Select(i=>P("RAW_OLD_"+i.ToString("D3"))));
var rawRaceNew=string.Concat(Enumerable.Range(0,120).Select(i=>P("RAW_NEW_"+i.ToString("D3"))));
Make(rawRaceA,rawRaceOld);Make(rawRaceB,rawRaceNew);
var rawRacePadding=new byte[24*1024*1024];new Random(5218).NextBytes(rawRacePadding);
AddWordBytes(rawRaceB,"word/media/race-padding.bin",rawRacePadding);
var rawRaceSentinel=Encoding.UTF8.GetBytes("PREVIOUS_RAW_RACE_WORD");File.WriteAllBytes(rawRaceO,rawRaceSentinel);
var rawRaceTask=eng.ExportWordAsync(rawRaceA,rawRaceB,rawRaceO,"T",true);
var rawRacePattern="."+Path.GetFileName(rawRaceO)+".*.tmp";
for(var i=0;i<4000&&!Directory.GetFiles(dir,rawRacePattern).Any()&&!rawRaceTask.IsCompleted;i++)await Task.Delay(1);
using(var fsRaw=new FileStream(rawRaceA,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite))
{
    fsRaw.Position=Math.Max(0,fsRaw.Length-16);
    fsRaw.WriteByte(0x20);
}
var rawRaceBlocked=false;
try{await rawRaceTask;}
catch(InvalidOperationException ex){rawRaceBlocked=ex.Message.Contains("입력 파일이 변경")||ex.Message.Contains("내보내기 중");}
catch(InvalidDataException){rawRaceBlocked=true;}
Check(rawRaceBlocked,"Word export without ComparisonResult committed after source mutation");
Check(File.ReadAllBytes(rawRaceO).SequenceEqual(rawRaceSentinel),"raw Word race guard replaced prior output");
Console.WriteLine("PASS WORD RAW MID-EXPORT SOURCE RACE GUARD");


// 96. Internal Word hyperlink anchor changes are semantic even without an r:id.
var intLinkA=Path.Combine(dir,"internalLinkA.docx");var intLinkB=Path.Combine(dir,"internalLinkB.docx");var intLinkO=Path.Combine(dir,"internalLinkOut.docx");
Make(intLinkA,P("Jump")+P("Target A")+P("Target B"));Make(intLinkB,P("Jump")+P("Target A")+P("Target B"));
var intLinkDocA="<w:document xmlns:w=\""+W+"\"><w:body><w:p><w:hyperlink w:anchor=\"TargetA\"><w:r><w:t>Jump</w:t></w:r></w:hyperlink></w:p><w:p><w:bookmarkStart w:id=\"1\" w:name=\"TargetA\"/><w:r><w:t>Target A</w:t></w:r><w:bookmarkEnd w:id=\"1\"/></w:p><w:p><w:bookmarkStart w:id=\"2\" w:name=\"TargetB\"/><w:r><w:t>Target B</w:t></w:r><w:bookmarkEnd w:id=\"2\"/></w:p><w:sectPr/></w:body></w:document>";
var intLinkDocB=intLinkDocA.Replace("w:anchor=\"TargetA\"","w:anchor=\"TargetB\"");
AddWordXml(intLinkA,"word/document.xml",intLinkDocA);AddWordXml(intLinkB,"word/document.xml",intLinkDocB);
var intLinkBlocked=false;try{await eng.ExportWordAsync(intLinkA,intLinkB,intLinkO,"T",true);}catch(InvalidOperationException ex){intLinkBlocked=ex.Message.Contains("비텍스트")||ex.Message.Contains("관계");}
Check(intLinkBlocked,"internal hyperlink anchor target change was silently ignored");
Console.WriteLine("PASS INTERNAL HYPERLINK ANCHOR GUARD");

// 97. Moving the bookmark targeted by an unchanged internal hyperlink must be detected.
var bmMoveA=Path.Combine(dir,"bookmarkMoveA.docx");var bmMoveB=Path.Combine(dir,"bookmarkMoveB.docx");var bmMoveO=Path.Combine(dir,"bookmarkMoveOut.docx");
Make(bmMoveA,P("Jump")+P("Alpha")+P("Beta"));Make(bmMoveB,P("Jump")+P("Alpha")+P("Beta"));
var bmMoveDocA="<w:document xmlns:w=\""+W+"\"><w:body><w:p><w:hyperlink w:anchor=\"Dest\"><w:r><w:t>Jump</w:t></w:r></w:hyperlink></w:p><w:p><w:bookmarkStart w:id=\"1\" w:name=\"Dest\"/><w:r><w:t>Alpha</w:t></w:r><w:bookmarkEnd w:id=\"1\"/></w:p>"+P("Beta")+"<w:sectPr/></w:body></w:document>";
var bmMoveDocB="<w:document xmlns:w=\""+W+"\"><w:body><w:p><w:hyperlink w:anchor=\"Dest\"><w:r><w:t>Jump</w:t></w:r></w:hyperlink></w:p>"+P("Alpha")+"<w:p><w:bookmarkStart w:id=\"1\" w:name=\"Dest\"/><w:r><w:t>Beta</w:t></w:r><w:bookmarkEnd w:id=\"1\"/></w:p><w:sectPr/></w:body></w:document>";
AddWordXml(bmMoveA,"word/document.xml",bmMoveDocA);AddWordXml(bmMoveB,"word/document.xml",bmMoveDocB);
var bmMoveBlocked=false;try{await eng.ExportWordAsync(bmMoveA,bmMoveB,bmMoveO,"T",true);}catch(InvalidOperationException ex){bmMoveBlocked=ex.Message.Contains("비텍스트")||ex.Message.Contains("관계");}
Check(bmMoveBlocked,"linked bookmark target moved without being detected");
Console.WriteLine("PASS LINKED BOOKMARK LOCATION GUARD");

// 98. Comment text changes must not be silently inherited from B without tracking.
var commentA=Path.Combine(dir,"commentA.docx");var commentB=Path.Combine(dir,"commentB.docx");var commentO=Path.Combine(dir,"commentOut.docx");
var commentBody="<w:document xmlns:w=\""+W+"\"><w:body><w:p><w:commentRangeStart w:id=\"0\"/><w:r><w:t>Alpha</w:t></w:r><w:commentRangeEnd w:id=\"0\"/><w:r><w:commentReference w:id=\"0\"/></w:r></w:p><w:sectPr/></w:body></w:document>";
Make(commentA,P("Alpha"));Make(commentB,P("Alpha"));AddWordXml(commentA,"word/document.xml",commentBody);AddWordXml(commentB,"word/document.xml",commentBody);
AddWordXml(commentA,"word/comments.xml","<w:comments xmlns:w=\""+W+"\"><w:comment w:id=\"0\" w:author=\"T\"><w:p><w:r><w:t>Old comment</w:t></w:r></w:p></w:comment></w:comments>");
AddWordXml(commentB,"word/comments.xml","<w:comments xmlns:w=\""+W+"\"><w:comment w:id=\"0\" w:author=\"T\"><w:p><w:r><w:t>New comment</w:t></w:r></w:p></w:comment></w:comments>");
var commentBlocked=false;try{await eng.ExportWordAsync(commentA,commentB,commentO,"T",true);}catch(InvalidOperationException ex){commentBlocked=ex.Message.Contains("comments")||ex.Message.Contains("서로 다릅니다");}
Check(commentBlocked,"comment content change was silently omitted");
Console.WriteLine("PASS COMMENT CONTENT GUARD");

// 99. Moving an unchanged comment range inside the same paragraph must be detected.
var commentMoveA=Path.Combine(dir,"commentMoveA.docx");var commentMoveB=Path.Combine(dir,"commentMoveB.docx");var commentMoveO=Path.Combine(dir,"commentMoveOut.docx");
Make(commentMoveA,P("Alpha Beta"));Make(commentMoveB,P("Alpha Beta"));
var commentMoveDocA="<w:document xmlns:w=\""+W+"\"><w:body><w:p><w:commentRangeStart w:id=\"0\"/><w:r><w:t xml:space=\"preserve\">Alpha </w:t></w:r><w:commentRangeEnd w:id=\"0\"/><w:r><w:t>Beta</w:t></w:r><w:r><w:commentReference w:id=\"0\"/></w:r></w:p><w:sectPr/></w:body></w:document>";
var commentMoveDocB="<w:document xmlns:w=\""+W+"\"><w:body><w:p><w:r><w:t xml:space=\"preserve\">Alpha </w:t></w:r><w:commentRangeStart w:id=\"0\"/><w:r><w:t>Beta</w:t></w:r><w:commentRangeEnd w:id=\"0\"/><w:r><w:commentReference w:id=\"0\"/></w:r></w:p><w:sectPr/></w:body></w:document>";
AddWordXml(commentMoveA,"word/document.xml",commentMoveDocA);AddWordXml(commentMoveB,"word/document.xml",commentMoveDocB);
var sameComments="<w:comments xmlns:w=\""+W+"\"><w:comment w:id=\"0\" w:author=\"T\"><w:p><w:r><w:t>Same comment</w:t></w:r></w:p></w:comment></w:comments>";
AddWordXml(commentMoveA,"word/comments.xml",sameComments);AddWordXml(commentMoveB,"word/comments.xml",sameComments);
var commentMoveBlocked=false;try{await eng.ExportWordAsync(commentMoveA,commentMoveB,commentMoveO,"T",true);}catch(InvalidOperationException ex){commentMoveBlocked=ex.Message.Contains("비텍스트")||ex.Message.Contains("서로 다릅니다");}
Check(commentMoveBlocked,"comment range moved within paragraph without being detected");
Console.WriteLine("PASS COMMENT RANGE LOCATION GUARD");


// 100. Internal hyperlink display text may be edited while the internal target stays unchanged.
var intTextA=Path.Combine(dir,"internalTextA.docx");var intTextB=Path.Combine(dir,"internalTextB.docx");var intTextO=Path.Combine(dir,"internalTextOut.docx");
Make(intTextA,P("Jump")+P("Destination"));Make(intTextB,P("Jump changed")+P("Destination"));
var intTextDocA="<w:document xmlns:w=\""+W+"\"><w:body><w:p><w:hyperlink w:anchor=\"Dest\"><w:r><w:t>Jump</w:t></w:r></w:hyperlink></w:p><w:p><w:bookmarkStart w:id=\"1\" w:name=\"Dest\"/><w:r><w:t>Destination</w:t></w:r><w:bookmarkEnd w:id=\"1\"/></w:p><w:sectPr/></w:body></w:document>";
var intTextDocB=intTextDocA.Replace(">Jump<",">Jump changed<");
AddWordXml(intTextA,"word/document.xml",intTextDocA);AddWordXml(intTextB,"word/document.xml",intTextDocB);
await eng.ExportWordAsync(intTextA,intTextB,intTextO,"T",true);
Check(File.Exists(intTextO),"internal hyperlink display-text edit was falsely blocked");
Console.WriteLine("PASS INTERNAL HYPERLINK DISPLAY-TEXT EDIT");

// 101. A linked bookmark may remain at the same structural target while that paragraph is fully rewritten.
var bmEditA=Path.Combine(dir,"bookmarkEditA.docx");var bmEditB=Path.Combine(dir,"bookmarkEditB.docx");var bmEditO=Path.Combine(dir,"bookmarkEditOut.docx");
Make(bmEditA,P("Jump")+P("Alpha old wording")+P("Tail"));Make(bmEditB,P("Jump")+P("Completely rewritten destination")+P("Tail"));
var bmEditDocA="<w:document xmlns:w=\""+W+"\"><w:body><w:p><w:hyperlink w:anchor=\"Dest\"><w:r><w:t>Jump</w:t></w:r></w:hyperlink></w:p><w:p><w:bookmarkStart w:id=\"1\" w:name=\"Dest\"/><w:r><w:t>Alpha old wording</w:t></w:r><w:bookmarkEnd w:id=\"1\"/></w:p>"+P("Tail")+"<w:sectPr/></w:body></w:document>";
var bmEditDocB="<w:document xmlns:w=\""+W+"\"><w:body><w:p><w:hyperlink w:anchor=\"Dest\"><w:r><w:t>Jump</w:t></w:r></w:hyperlink></w:p><w:p><w:bookmarkStart w:id=\"1\" w:name=\"Dest\"/><w:r><w:t>Completely rewritten destination</w:t></w:r><w:bookmarkEnd w:id=\"1\"/></w:p>"+P("Tail")+"<w:sectPr/></w:body></w:document>";
AddWordXml(bmEditA,"word/document.xml",bmEditDocA);AddWordXml(bmEditB,"word/document.xml",bmEditDocB);
await eng.ExportWordAsync(bmEditA,bmEditB,bmEditO,"T",true);
Check(File.Exists(bmEditO),"unchanged linked bookmark was falsely blocked by target paragraph rewrite");
Console.WriteLine("PASS LINKED BOOKMARK OWNER REWRITE");

// 102. An unchanged comment range may surround edited text; the text edit itself should remain trackable.
var commentEditA=Path.Combine(dir,"commentEditA.docx");var commentEditB=Path.Combine(dir,"commentEditB.docx");var commentEditO=Path.Combine(dir,"commentEditOut.docx");
Make(commentEditA,P("Alpha old"));Make(commentEditB,P("Completely rewritten"));
var commentEditDocA="<w:document xmlns:w=\""+W+"\"><w:body><w:p><w:commentRangeStart w:id=\"0\"/><w:r><w:t>Alpha old</w:t></w:r><w:commentRangeEnd w:id=\"0\"/><w:r><w:commentReference w:id=\"0\"/></w:r></w:p><w:sectPr/></w:body></w:document>";
var commentEditDocB="<w:document xmlns:w=\""+W+"\"><w:body><w:p><w:commentRangeStart w:id=\"0\"/><w:r><w:t>Completely rewritten</w:t></w:r><w:commentRangeEnd w:id=\"0\"/><w:r><w:commentReference w:id=\"0\"/></w:r></w:p><w:sectPr/></w:body></w:document>";
AddWordXml(commentEditA,"word/document.xml",commentEditDocA);AddWordXml(commentEditB,"word/document.xml",commentEditDocB);
AddWordXml(commentEditA,"word/comments.xml",sameComments);AddWordXml(commentEditB,"word/comments.xml",sameComments);
await eng.ExportWordAsync(commentEditA,commentEditB,commentEditO,"T",true);
Check(File.Exists(commentEditO),"unchanged comment range was falsely blocked by commented text rewrite");
Console.WriteLine("PASS COMMENT OWNER TEXT REWRITE");


// 103. Korean spacing-only edits must report the changed word boundary, not "없이" -> "없이".
var spacingA=Path.Combine(dir,"spacingA.txt");var spacingB=Path.Combine(dir,"spacingB.txt");
File.WriteAllText(spacingA,"이용자의 동의없이 제3자에게 제공할 수 있습니다.",new UTF8Encoding(false));
File.WriteAllText(spacingB,"이용자의 동의 없이 제3자에게 제공할 수 있습니다.",new UTF8Encoding(false));
var spacingCmp=await eng.CompareAsync(new[]{spacingA,spacingB},0,"general",true,true);
var spacingMsgs=spacingCmp.Rows.SelectMany(r=>r.DisplayMessages).ToList();
Check(spacingMsgs.Any(m=>m.Contains("동의없이")&&m.Contains("동의 없이")),
    "spacing insertion was not reported with lexical context: "+string.Join(" | ",spacingMsgs));
Check(!spacingMsgs.Any(m=>m.Contains("“없이” → “없이”")),
    "spacing insertion regressed to meaningless identical-text marker: "+string.Join(" | ",spacingMsgs));
Console.WriteLine("PASS KOREAN SPACING INSERTION DIFF");

// 104. The reverse spacing edit must also preserve the lexical boundary in the message.
var spacingRev=await eng.CompareAsync(new[]{spacingB,spacingA},0,"general",true,true);
var spacingRevMsgs=spacingRev.Rows.SelectMany(r=>r.DisplayMessages).ToList();
Check(spacingRevMsgs.Any(m=>m.Contains("동의 없이")&&m.Contains("동의없이")),
    "spacing deletion was not reported with lexical context: "+string.Join(" | ",spacingRevMsgs));
Check(!spacingRevMsgs.Any(m=>m.Contains("“없이” → “없이”")),
    "spacing deletion regressed to meaningless identical-text marker: "+string.Join(" | ",spacingRevMsgs));
Console.WriteLine("PASS KOREAN SPACING DELETION DIFF");


// 105. Format-only DOCX differences must not block content Track Changes export.
// DOCX->DOCX intentionally preserves revised B formatting as the physical base.
var fmtA=Path.Combine(dir,"formatOnlyA.docx");var fmtB=Path.Combine(dir,"formatOnlyB.docx");var fmtO=Path.Combine(dir,"formatOnlyOut.docx");
Make(fmtA,"<w:p><w:r><w:rPr><w:b/></w:rPr><w:t>Alpha</w:t></w:r></w:p>");
Make(fmtB,P("Alpha"));
await eng.ExportWordAsync(fmtA,fmtB,fmtO,"T",true);
Check(File.Exists(fmtO),"format-only Word difference incorrectly blocked export");
Console.WriteLine("PASS FORMATTING-ONLY EXPORT ALLOWED");

// 106. Zero-width format control immediately after flattened labels must not destroy boundaries.
var zwAfterLabel=(System.Collections.IEnumerable)parse.Invoke(null,new object[]{
    "Lead: (1)​ One (2)​ Two (3)​ Three"
})!;
var zwAfterLabels=new List<string>();
foreach(var x in zwAfterLabel) zwAfterLabels.Add((string)x!.GetType().GetProperty("Label")!.GetValue(x)!);
for(var n=1;n<=3;n++)
    Check(zwAfterLabels.Contains("("+n+")"),"zero-width-after-label boundary missing: ("+n+") => "+string.Join(",",zwAfterLabels));
Console.WriteLine("PASS ZERO-WIDTH AFTER ITEM LABEL");

// 107. A soft hyphen must not disappear silently from comparison/export semantics.
var shyA=Path.Combine(dir,"softHyphenA.docx");var shyB=Path.Combine(dir,"softHyphenB.docx");var shyO=Path.Combine(dir,"softHyphenOut.docx");
Make(shyA,"<w:p><w:r><w:t>Alpha</w:t><w:softHyphen/><w:t>Beta</w:t></w:r></w:p>");
Make(shyB,P("AlphaBeta"));
var shyCmp=await eng.CompareAsync(new[]{shyA,shyB},0,"general",true,true);
var shySafe=shyCmp.Rows.Any(r=>r.Changed);
try
{
    await eng.ExportWordAsync(shyA,shyB,shyO,"T",true);
    var shyDoc=Doc(shyO);
    shySafe=shySafe||shyDoc.Descendants(w+"del").Any()||shyDoc.Descendants(w+"ins").Any();
}
catch(InvalidOperationException){shySafe=true;}
Check(shySafe,"w:softHyphen disappeared without comparison marker, tracked revision, or export rejection");
Console.WriteLine("PASS SOFT-HYPHEN SAFETY");


// 108. An unchanged soft hyphen in both documents must remain exportable.
var shySameA=Path.Combine(dir,"softHyphenSameA.docx");var shySameB=Path.Combine(dir,"softHyphenSameB.docx");var shySameO=Path.Combine(dir,"softHyphenSameOut.docx");
Make(shySameA,"<w:p><w:r><w:t>Alpha</w:t><w:softHyphen/><w:t>Beta</w:t></w:r></w:p>");
Make(shySameB,"<w:p><w:r><w:t>Alpha</w:t><w:softHyphen/><w:t>Beta</w:t></w:r></w:p>");
await eng.ExportWordAsync(shySameA,shySameB,shySameO,"T",true);
Check(File.Exists(shySameO),"unchanged w:softHyphen was falsely blocked");
Console.WriteLine("PASS SOFT-HYPHEN UNCHANGED");


// 109. Deleting a manual line break must preserve the structural w:br inside w:del.
// A literal LF in w:delText is not the same WordprocessingML construct.
var brDelA=Path.Combine(dir,"brDeleteA.docx");var brDelB=Path.Combine(dir,"brDeleteB.docx");var brDelO=Path.Combine(dir,"brDeleteOut.docx");
Make(brDelA,"<w:p><w:r><w:t>Alpha</w:t><w:br/><w:t>Beta</w:t></w:r></w:p>");
Make(brDelB,P("AlphaBeta"));
await eng.ExportWordAsync(brDelA,brDelB,brDelO,"T",true);
var brDelDoc=Doc(brDelO);
var brDelNodes=brDelDoc.Descendants(w+"del").ToList();
Check(brDelNodes.Any(x=>x.Descendants(w+"br").Any()),
    "deleted manual line break was not represented as w:br inside w:del");
Console.WriteLine("PASS MANUAL-BREAK DELETION STRUCTURE");

// 110. Deleting a tab must preserve w:tab inside w:del.
var tabDelA=Path.Combine(dir,"tabDeleteA.docx");var tabDelB=Path.Combine(dir,"tabDeleteB.docx");var tabDelO=Path.Combine(dir,"tabDeleteOut.docx");
Make(tabDelA,"<w:p><w:r><w:t>Alpha</w:t><w:tab/><w:t>Beta</w:t></w:r></w:p>");
Make(tabDelB,P("AlphaBeta"));
await eng.ExportWordAsync(tabDelA,tabDelB,tabDelO,"T",true);
var tabDelDoc=Doc(tabDelO);
var tabDelNodes=tabDelDoc.Descendants(w+"del").ToList();
Check(tabDelNodes.Any(x=>x.Descendants(w+"tab").Any()),
    "deleted tab was not represented as w:tab inside w:del");
Console.WriteLine("PASS TAB DELETION STRUCTURE");

// 111. Deleting a no-break hyphen must not silently degrade to ordinary '-' text.
var nbhDelA=Path.Combine(dir,"nbhDeleteA.docx");var nbhDelB=Path.Combine(dir,"nbhDeleteB.docx");var nbhDelO=Path.Combine(dir,"nbhDeleteOut.docx");
Make(nbhDelA,"<w:p><w:r><w:t>Alpha</w:t><w:noBreakHyphen/><w:t>Beta</w:t></w:r></w:p>");
Make(nbhDelB,P("AlphaBeta"));
var nbhDeleteSafe=false;
try
{
    await eng.ExportWordAsync(nbhDelA,nbhDelB,nbhDelO,"T",true);
    var nbhDelDoc=Doc(nbhDelO);
    nbhDeleteSafe=nbhDelDoc.Descendants(w+"del").Any(x=>x.Descendants(w+"noBreakHyphen").Any());
}
catch(InvalidOperationException){nbhDeleteSafe=true;}
Check(nbhDeleteSafe,"deleted no-break hyphen silently degraded to ordinary text");
Console.WriteLine("PASS NO-BREAK-HYPHEN DELETION SAFETY");


// 112-115. Non-paragraph formatting differences are also allowed because revised B is the
// physical base. These tests protect against reintroducing the v5.20.20 overblocking guard.
static async Task<bool> FormattingExportAllowed(NativeComparisonEngine engine,string a,string b,string o)
{
    await engine.ExportWordAsync(a,b,o,"T",true);
    return File.Exists(o);
}

var tblFmtA=Path.Combine(dir,"tableFmtA.docx");var tblFmtB=Path.Combine(dir,"tableFmtB.docx");var tblFmtO=Path.Combine(dir,"tableFmtOut.docx");
Make(tblFmtA,"<w:tbl><w:tblPr><w:tblW w:w='5000' w:type='dxa'/></w:tblPr><w:tblGrid><w:gridCol/></w:tblGrid><w:tr><w:tc><w:tcPr/><w:p><w:r><w:t>Alpha</w:t></w:r></w:p></w:tc></w:tr></w:tbl>");
Make(tblFmtB,"<w:tbl><w:tblPr><w:tblW w:w='6000' w:type='dxa'/></w:tblPr><w:tblGrid><w:gridCol/></w:tblGrid><w:tr><w:tc><w:tcPr/><w:p><w:r><w:t>Alpha</w:t></w:r></w:p></w:tc></w:tr></w:tbl>");
var tblFmtSafe=await FormattingExportAllowed(eng,tblFmtA,tblFmtB,tblFmtO);

var cellFmtA=Path.Combine(dir,"cellFmtA.docx");var cellFmtB=Path.Combine(dir,"cellFmtB.docx");var cellFmtO=Path.Combine(dir,"cellFmtOut.docx");
Make(cellFmtA,"<w:tbl><w:tblPr/><w:tblGrid><w:gridCol/></w:tblGrid><w:tr><w:tc><w:tcPr><w:shd w:fill='FFFFFF'/></w:tcPr><w:p><w:r><w:t>Alpha</w:t></w:r></w:p></w:tc></w:tr></w:tbl>");
Make(cellFmtB,"<w:tbl><w:tblPr/><w:tblGrid><w:gridCol/></w:tblGrid><w:tr><w:tc><w:tcPr><w:shd w:fill='EEEEEE'/></w:tcPr><w:p><w:r><w:t>Alpha</w:t></w:r></w:p></w:tc></w:tr></w:tbl>");
var cellFmtSafe=await FormattingExportAllowed(eng,cellFmtA,cellFmtB,cellFmtO);

var sectFmtA=Path.Combine(dir,"sectionFmtA.docx");var sectFmtB=Path.Combine(dir,"sectionFmtB.docx");var sectFmtO=Path.Combine(dir,"sectionFmtOut.docx");
Make(sectFmtA,P("Alpha"));
Make(sectFmtB,P("Alpha"));
AddWordXml(sectFmtA,"word/document.xml","<w:document xmlns:w='"+W+"'><w:body>"+P("Alpha")+"<w:sectPr><w:pgMar w:top='1440' w:right='1440' w:bottom='1440' w:left='1440'/></w:sectPr></w:body></w:document>");
AddWordXml(sectFmtB,"word/document.xml","<w:document xmlns:w='"+W+"'><w:body>"+P("Alpha")+"<w:sectPr><w:pgMar w:top='720' w:right='1440' w:bottom='1440' w:left='1440'/></w:sectPr></w:body></w:document>");
var sectFmtSafe=await FormattingExportAllowed(eng,sectFmtA,sectFmtB,sectFmtO);

var styleFmtA=Path.Combine(dir,"styleFmtA.docx");var styleFmtB=Path.Combine(dir,"styleFmtB.docx");var styleFmtO=Path.Combine(dir,"styleFmtOut.docx");
Make(styleFmtA,"<w:p><w:pPr><w:pStyle w:val='MyStyle'/></w:pPr><w:r><w:t>Alpha</w:t></w:r></w:p>");
Make(styleFmtB,"<w:p><w:pPr><w:pStyle w:val='MyStyle'/></w:pPr><w:r><w:t>Alpha</w:t></w:r></w:p>");
AddWordXml(styleFmtA,"word/styles.xml","<w:styles xmlns:w='"+W+"'><w:style w:type='paragraph' w:styleId='MyStyle'><w:name w:val='MyStyle'/><w:rPr><w:b/></w:rPr></w:style></w:styles>");
AddWordXml(styleFmtB,"word/styles.xml","<w:styles xmlns:w='"+W+"'><w:style w:type='paragraph' w:styleId='MyStyle'><w:name w:val='MyStyle'/><w:rPr><w:i/></w:rPr></w:style></w:styles>");
var styleFmtSafe=await FormattingExportAllowed(eng,styleFmtA,styleFmtB,styleFmtO);

Check(tblFmtSafe,"table formatting difference incorrectly blocked export");
Console.WriteLine("PASS TABLE-FORMATTING EXPORT ALLOWED");
Check(cellFmtSafe,"cell formatting difference incorrectly blocked export");
Console.WriteLine("PASS CELL-FORMATTING EXPORT ALLOWED");
Check(sectFmtSafe,"section formatting difference incorrectly blocked export");
Console.WriteLine("PASS SECTION-FORMATTING EXPORT ALLOWED");
Check(styleFmtSafe,"referenced style difference incorrectly blocked export");
Console.WriteLine("PASS REFERENCED-STYLE EXPORT ALLOWED");


// 116. Default paragraph style changes are preserved from revised B and must not block export.
var defaultStyleA=Path.Combine(dir,"defaultStyleA.docx");var defaultStyleB=Path.Combine(dir,"defaultStyleB.docx");var defaultStyleO=Path.Combine(dir,"defaultStyleOut.docx");
Make(defaultStyleA,P("Alpha"));Make(defaultStyleB,P("Alpha"));
AddWordXml(defaultStyleA,"word/styles.xml","<w:styles xmlns:w='"+W+"'><w:style w:type='paragraph' w:default='1' w:styleId='Normal'><w:name w:val='Normal'/><w:rPr><w:b/></w:rPr></w:style></w:styles>");
AddWordXml(defaultStyleB,"word/styles.xml","<w:styles xmlns:w='"+W+"'><w:style w:type='paragraph' w:default='1' w:styleId='Normal'><w:name w:val='Normal'/><w:rPr><w:i/></w:rPr></w:style></w:styles>");
await eng.ExportWordAsync(defaultStyleA,defaultStyleB,defaultStyleO,"T",true);
Check(File.Exists(defaultStyleO),"default paragraph style difference incorrectly blocked export");
Console.WriteLine("PASS DEFAULT-STYLE EXPORT ALLOWED");

// 117. An inserted duplicate-text row with different formatting must not make the formatting guard
// pair the new row to the old survivor and falsely reject a legitimate row insertion.
var dupFmtA=Path.Combine(dir,"dupFmtA.docx");var dupFmtB=Path.Combine(dir,"dupFmtB.docx");var dupFmtO=Path.Combine(dir,"dupFmtOut.docx");
Make(dupFmtA,"<w:tbl><w:tblPr/><w:tblGrid><w:gridCol/></w:tblGrid><w:tr><w:tc><w:tcPr><w:shd w:fill='FFFFFF'/></w:tcPr><w:p><w:r><w:t>Same</w:t></w:r></w:p></w:tc></w:tr></w:tbl>");
Make(dupFmtB,"<w:tbl><w:tblPr/><w:tblGrid><w:gridCol/></w:tblGrid><w:tr><w:tc><w:tcPr><w:shd w:fill='EEEEEE'/></w:tcPr><w:p><w:r><w:t>Same</w:t></w:r></w:p></w:tc></w:tr><w:tr><w:tc><w:tcPr><w:shd w:fill='FFFFFF'/></w:tcPr><w:p><w:r><w:t>Same</w:t></w:r></w:p></w:tc></w:tr></w:tbl>");
var dupFmtExported=true;
try{await eng.ExportWordAsync(dupFmtA,dupFmtB,dupFmtO,"T",true);}
catch(InvalidOperationException){dupFmtExported=false;}
Check(dupFmtExported,"formatting guard falsely rejected duplicate-text row insertion");
var dupFmtDoc=Doc(dupFmtO);
Check(dupFmtDoc.Descendants(w+"tr").Any(x=>x.Element(w+"trPr")?.Element(w+"ins") is not null),
    "duplicate-text inserted row was not tracked at row level");
Console.WriteLine("PASS DUPLICATE-TEXT ROW INSERTION FORMATTING SAFETY");


// 118. A used table style definition change is preserved from revised B.
var tblStyleA=Path.Combine(dir,"tblStyleA.docx");var tblStyleB=Path.Combine(dir,"tblStyleB.docx");var tblStyleO=Path.Combine(dir,"tblStyleOut.docx");
var styledTable="<w:tbl><w:tblPr><w:tblStyle w:val='GridStyle'/></w:tblPr><w:tblGrid><w:gridCol/></w:tblGrid><w:tr><w:tc><w:tcPr/><w:p><w:r><w:t>Alpha</w:t></w:r></w:p></w:tc></w:tr></w:tbl>";
Make(tblStyleA,styledTable);Make(tblStyleB,styledTable);
AddWordXml(tblStyleA,"word/styles.xml","<w:styles xmlns:w='"+W+"'><w:style w:type='table' w:styleId='GridStyle'><w:name w:val='GridStyle'/><w:tblPr><w:tblBorders><w:top w:val='single' w:sz='4'/></w:tblBorders></w:tblPr></w:style></w:styles>");
AddWordXml(tblStyleB,"word/styles.xml","<w:styles xmlns:w='"+W+"'><w:style w:type='table' w:styleId='GridStyle'><w:name w:val='GridStyle'/><w:tblPr><w:tblBorders><w:top w:val='double' w:sz='8'/></w:tblBorders></w:tblPr></w:style></w:styles>");
await eng.ExportWordAsync(tblStyleA,tblStyleB,tblStyleO,"T",true);
Check(File.Exists(tblStyleO),"table style definition difference incorrectly blocked export");
Console.WriteLine("PASS TABLE-STYLE DEFINITION ALLOWED");

// 119. Theme palette changes are preserved from revised B and must not block export.
var themeFmtA=Path.Combine(dir,"themeFmtA.docx");var themeFmtB=Path.Combine(dir,"themeFmtB.docx");var themeFmtO=Path.Combine(dir,"themeFmtOut.docx");
var themeBody="<w:p><w:r><w:rPr><w:color w:themeColor='accent1'/></w:rPr><w:t>Alpha</w:t></w:r></w:p>";
Make(themeFmtA,themeBody);Make(themeFmtB,themeBody);
const string A_NS="http://schemas.openxmlformats.org/drawingml/2006/main";
AddWordXml(themeFmtA,"word/theme/theme1.xml","<a:theme xmlns:a='"+A_NS+"' name='T'><a:themeElements><a:clrScheme name='C'><a:dk1><a:srgbClr val='000000'/></a:dk1><a:lt1><a:srgbClr val='FFFFFF'/></a:lt1><a:accent1><a:srgbClr val='FF0000'/></a:accent1></a:clrScheme></a:themeElements></a:theme>");
AddWordXml(themeFmtB,"word/theme/theme1.xml","<a:theme xmlns:a='"+A_NS+"' name='T'><a:themeElements><a:clrScheme name='C'><a:dk1><a:srgbClr val='000000'/></a:dk1><a:lt1><a:srgbClr val='FFFFFF'/></a:lt1><a:accent1><a:srgbClr val='0000FF'/></a:accent1></a:clrScheme></a:themeElements></a:theme>");
await eng.ExportWordAsync(themeFmtA,themeFmtB,themeFmtO,"T",true);
Check(File.Exists(themeFmtO),"theme formatting difference incorrectly blocked export");
Console.WriteLine("PASS THEME-FORMATTING ALLOWED");

// 120. Numbering layout/appearance differences are preserved from revised B.
var numFmtA=Path.Combine(dir,"numFmtA.docx");var numFmtB=Path.Combine(dir,"numFmtB.docx");var numFmtO=Path.Combine(dir,"numFmtOut.docx");
var numberedP="<w:p><w:pPr><w:numPr><w:ilvl w:val='0'/><w:numId w:val='1'/></w:numPr></w:pPr><w:r><w:t>Alpha</w:t></w:r></w:p>";
Make(numFmtA,numberedP);Make(numFmtB,numberedP);
AddWordXml(numFmtA,"word/numbering.xml","<w:numbering xmlns:w='"+W+"'><w:abstractNum w:abstractNumId='1'><w:lvl w:ilvl='0'><w:start w:val='1'/><w:numFmt w:val='decimal'/><w:lvlText w:val='%1.'/><w:pPr><w:ind w:left='720' w:hanging='360'/></w:pPr></w:lvl></w:abstractNum><w:num w:numId='1'><w:abstractNumId w:val='1'/></w:num></w:numbering>");
AddWordXml(numFmtB,"word/numbering.xml","<w:numbering xmlns:w='"+W+"'><w:abstractNum w:abstractNumId='1'><w:lvl w:ilvl='0'><w:start w:val='1'/><w:numFmt w:val='decimal'/><w:lvlText w:val='%1.'/><w:pPr><w:ind w:left='1440' w:hanging='360'/></w:pPr></w:lvl></w:abstractNum><w:num w:numId='1'><w:abstractNumId w:val='1'/></w:num></w:numbering>");
await eng.ExportWordAsync(numFmtA,numFmtB,numFmtO,"T",true);
Check(File.Exists(numFmtO),"numbering formatting difference incorrectly blocked export");
Console.WriteLine("PASS NUMBERING-FORMATTING ALLOWED");

// 121. A theme change that is not referenced by surviving document formatting must not
// falsely block tracked export.
var unusedThemeA=Path.Combine(dir,"unusedThemeA.docx");var unusedThemeB=Path.Combine(dir,"unusedThemeB.docx");var unusedThemeO=Path.Combine(dir,"unusedThemeOut.docx");
Make(unusedThemeA,P("Alpha"));Make(unusedThemeB,P("Alpha"));
AddWordXml(unusedThemeA,"word/theme/theme1.xml","<a:theme xmlns:a='"+A_NS+"' name='T'><a:themeElements><a:clrScheme name='C'><a:dk1><a:srgbClr val='000000'/></a:dk1><a:lt1><a:srgbClr val='FFFFFF'/></a:lt1><a:accent1><a:srgbClr val='FF0000'/></a:accent1></a:clrScheme></a:themeElements></a:theme>");
AddWordXml(unusedThemeB,"word/theme/theme1.xml","<a:theme xmlns:a='"+A_NS+"' name='T'><a:themeElements><a:clrScheme name='C'><a:dk1><a:srgbClr val='000000'/></a:dk1><a:lt1><a:srgbClr val='FFFFFF'/></a:lt1><a:accent1><a:srgbClr val='0000FF'/></a:accent1></a:clrScheme></a:themeElements></a:theme>");
await eng.ExportWordAsync(unusedThemeA,unusedThemeB,unusedThemeO,"T",true);
Check(File.Exists(unusedThemeO),"unused theme change was falsely blocked");
Console.WriteLine("PASS UNUSED-THEME CHANGE ALLOWED");

// 122. Replacing one non-empty numPr with another while the rendered label stays the same is a
// formatting/numbering-definition difference and is preserved from revised B.
var numRefA=Path.Combine(dir,"numRefA.docx");var numRefB=Path.Combine(dir,"numRefB.docx");var numRefO=Path.Combine(dir,"numRefOut.docx");
var numRefPA="<w:p><w:pPr><w:numPr><w:ilvl w:val='0'/><w:numId w:val='1'/></w:numPr></w:pPr><w:r><w:t>Alpha</w:t></w:r></w:p>";
var numRefPB="<w:p><w:pPr><w:numPr><w:ilvl w:val='0'/><w:numId w:val='2'/></w:numPr></w:pPr><w:r><w:t>Alpha</w:t></w:r></w:p>";
Make(numRefA,numRefPA);Make(numRefB,numRefPB);
var sameNumbering="<w:numbering xmlns:w='"+W+"'><w:abstractNum w:abstractNumId='1'><w:lvl w:ilvl='0'><w:start w:val='1'/><w:numFmt w:val='decimal'/><w:lvlText w:val='%1.'/></w:lvl></w:abstractNum><w:num w:numId='1'><w:abstractNumId w:val='1'/></w:num><w:num w:numId='2'><w:abstractNumId w:val='1'/></w:num></w:numbering>";
AddWordXml(numRefA,"word/numbering.xml",sameNumbering);AddWordXml(numRefB,"word/numbering.xml",sameNumbering);
await eng.ExportWordAsync(numRefA,numRefB,numRefO,"T",true);
Check(File.Exists(numRefO),"same-label non-empty numPr replacement incorrectly blocked export");
Console.WriteLine("PASS SAME-LABEL NUMPR REPLACEMENT ALLOWED");

// 123. Deleting a page/column/clear break must not silently degrade to an ordinary manual break.
var pageBreakA=Path.Combine(dir,"pageBreakA.docx");var pageBreakB=Path.Combine(dir,"pageBreakB.docx");var pageBreakO=Path.Combine(dir,"pageBreakOut.docx");
Make(pageBreakA,"<w:p><w:r><w:t>Alpha</w:t><w:br w:type='page'/><w:t>Beta</w:t></w:r></w:p>");
Make(pageBreakB,P("AlphaBeta"));
var pageBreakBlocked=false;
try{await eng.ExportWordAsync(pageBreakA,pageBreakB,pageBreakO,"T",true);}
catch(InvalidOperationException){pageBreakBlocked=true;}
Check(pageBreakBlocked,"page break deletion degraded to an ordinary manual break");
Console.WriteLine("PASS SPECIAL-BREAK DELETION SAFETY");


// 124. The on-screen comparison intentionally keeps the fine-grained V5.20.22 review model.
// Several distinct phrase/word replacements inside one numbered legal item remain separate
// reviewer markers; Word export is allowed to use a different presentation model.
var groupedA=Path.Combine(dir,"groupedItemA.txt");var groupedB=Path.Combine(dir,"groupedItemB.txt");
File.WriteAllText(groupedA,string.Join("\n",new[]{
    "제3조(개인정보의 보유기간 및 이용자 탈퇴)",
    "① 회사는 이용자로부터 수집한 개인정보를 이용자 자격이 유지되는 동안 보유 및 이용합니다.",
    "② 회사는 관련 법령에 의한 개인정보 보유 사유가 있는 경우에는 제1항에도 불구하고 상법, 전자상거래 등에서의 소비자보호에 관한 법률 등 관계법령의 규정에서 정한 일정한 기간 동안 개인정보를 보관합니다.",
    "1. 표시·광고에 관한 기록: 6개월",
    "2. 계약 또는 청약 철회 등에 관한 기록: 5년",
    "③ 이용자 탈퇴를 신청하시면 해당 계정은 즉시 탈퇴 처리됩니다."
}));
File.WriteAllText(groupedB,string.Join("\n",new[]{
    "제3조(개인정보의 보유기간 및 이용자 탈퇴)",
    "① 회사는 이용자로부터 수집한 개인정보를 이용자 자격이 유지되는 동안 보유 및 이용합니다.",
    "② 회사는 관계 법령에 따라 개인정보를 보존할 의무가 있는 경우 제1항에도 불구하고 해당 개인정보에 한하여 법령에서 정한 기간동안 개인정보를 보관할 수 있습니다.",
    "1. 표시·광고에 관한 기록: 6개월",
    "2. 계약 또는 청약 철회 등에 관한 기록: 5년",
    "③ 이용자 탈퇴를 신청하시면 해당 계정은 즉시 탈퇴 처리됩니다."
}));
var grouped=await eng.CompareAsync(new[]{groupedA,groupedB},0,"legal",true,true);
var groupedRow=grouped.Rows.FirstOrDefault(r=>r.Members[0]?.Number=="3") ?? grouped.Rows.First(r=>r.Members.Any(m=>m?.Text.Contains("제3조") == true));
Check(groupedRow.Markers.Count==6,"V5.20.22 UI review granularity changed: "+groupedRow.Markers.Count+" | "+string.Join(" | ",groupedRow.DisplayMessages));
Check(groupedRow.Markers.Any(m=>m.Message.Contains("관련")&&m.Message.Contains("관계")),"fine-grained legal-item replacement marker missing");
Check(groupedRow.Markers.Any(m=>m.Message.Contains("의한 개인정보 보유 사유가")&&m.Message.Contains("따라 개인정보를 보존할 의무가")),"second fine-grained legal-item replacement marker missing");
Console.WriteLine("PASS V5.20.22 UI REVIEW GRANULARITY");

// 125. Word Track Changes uses its own V5.20.24 export granularity. It may deliberately
// differ from the on-screen marker count while preserving readable stable anchors.
var balancedWordA=Path.Combine(dir,"balancedWordA.docx");var balancedWordB=Path.Combine(dir,"balancedWordB.docx");var balancedWordO=Path.Combine(dir,"balancedWordOut.docx");
var balancedOld="② 회사는 관련 법령에 의한 개인정보 보유 사유가 있는 경우에는 제1항에도 불구하고 상법, 전자상거래 등에서의 소비자보호에 관한 법률 등 관계법령의 규정에서 정한 일정한 기간 동안 개인정보를 보관합니다. 이 경우 회사는 보관하는 정보를 별도 분리 보관하며, 보관기간은 다음 각 호와 같습니다.";
var balancedNew="② 회사는 관계 법령에 따라 개인정보를 보존할 의무가 있는 경우 제1항에도 불구하고 해당 개인정보에 한하여 법령에서 정한 기간동안 개인정보를 보관할 수 있습니다. 이 경우 회사는 보관하는 정보를 별도 분리 보관하며, 보관기간은 다음 각 호와 같습니다.";
Make(balancedWordA,P(balancedOld));Make(balancedWordB,P(balancedNew));
var balancedCmp=await eng.CompareAsync(new[]{balancedWordA,balancedWordB},0,"legal",true,true);
await eng.ExportWordAsync(balancedWordA,balancedWordB,balancedWordO,"T",true,default,balancedCmp,0,1);
var balancedDoc=Doc(balancedWordO);
var balancedRevisions=balancedDoc.Descendants().Where(x=>x.Name==w+"ins"||x.Name==w+"del").ToList();
Check(balancedRevisions.Count is >=2 and <=8,"Word review granularity was over-fragmented or over-collapsed: revision nodes="+balancedRevisions.Count);
var plainText=string.Concat(balancedDoc.Descendants(w+"t").Where(t=>!t.Ancestors().Any(a=>a.Name==w+"ins"||a.Name==w+"del")).Select(t=>t.Value));
Check(plainText.Contains("제1항에도 불구하고")&&plainText.Contains("이 경우 회사는 보관하는 정보를 별도 분리 보관하며"),"Word review lost stable unchanged anchors between change hunks");
Console.WriteLine("PASS BALANCED WORD TRACK CHANGES: "+balancedRevisions.Count+" revision nodes");


// 126. Word export uses revised document B as the final visual baseline for matched paragraphs.
// Deleted text keeps A's direct run style, while inserted/surviving content keeps B's style.
var styleOutA=Path.Combine(dir,"styleOutA.docx");var styleOutB=Path.Combine(dir,"styleOutB.docx");var styleOutO=Path.Combine(dir,"styleOut.docx");
Make(styleOutA,"<w:p><w:pPr><w:spacing w:before='240'/><w:ind w:left='720'/><w:jc w:val='center'/></w:pPr><w:r><w:t xml:space='preserve'>Alpha </w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>OLD</w:t></w:r><w:r><w:t xml:space='preserve'> Omega</w:t></w:r></w:p>");
Make(styleOutB,"<w:p><w:pPr><w:spacing w:before='0'/><w:ind w:left='0'/><w:jc w:val='left'/></w:pPr><w:r><w:t xml:space='preserve'>Alpha </w:t></w:r><w:r><w:rPr><w:i/></w:rPr><w:t>NEW</w:t></w:r><w:r><w:t xml:space='preserve'> Omega</w:t></w:r></w:p>");
await eng.ExportWordAsync(styleOutA,styleOutB,styleOutO,"T",true);
var styleOutDoc=Doc(styleOutO);var styleOutP=styleOutDoc.Descendants(w+"p").First();var styleOutPPr=styleOutP.Element(w+"pPr");
Check(styleOutPPr?.Element(w+"spacing")?.Attribute(w+"before")?.Value=="0","Word export did not preserve revised paragraph spacing");
Check(styleOutPPr?.Element(w+"ind")?.Attribute(w+"left")?.Value=="0","Word export did not preserve revised paragraph indentation");
Check(styleOutPPr?.Element(w+"jc")?.Attribute(w+"val")?.Value=="left","Word export did not preserve revised paragraph alignment");
var oldDel=styleOutP.Descendants(w+"del").FirstOrDefault(x=>string.Concat(x.Descendants(w+"delText").Select(t=>t.Value)).Contains("OLD"));
Check(oldDel?.Descendants(w+"rPr").Any(r=>r.Element(w+"b") is not null)==true,"deleted text lost original run formatting");
var newIns=styleOutP.Descendants(w+"ins").FirstOrDefault(x=>string.Concat(x.Descendants(w+"t").Select(t=>t.Value)).Contains("NEW"));
Check(newIns?.Descendants(w+"rPr").Any(r=>r.Element(w+"i") is not null)==true,"inserted text lost revised run formatting");
Console.WriteLine("PASS WORD REVISED PARAGRAPH BASELINE + DELETED RUN STYLE");

// 127. A wholly deleted paragraph must retain its own original paragraph/run formatting rather
// than borrowing the nearest surviving paragraph's presentation.
var delStyleA=Path.Combine(dir,"delStyleA.docx");var delStyleB=Path.Combine(dir,"delStyleB.docx");var delStyleO=Path.Combine(dir,"delStyleOut.docx");
Make(delStyleA,P("KEEP")+"<w:p><w:pPr><w:spacing w:before='360'/><w:jc w:val='right'/></w:pPr><w:r><w:rPr><w:b/></w:rPr><w:t>DELETED_STYLE</w:t></w:r></w:p>"+P("AFTER"));
Make(delStyleB,P("KEEP")+P("AFTER"));
await eng.ExportWordAsync(delStyleA,delStyleB,delStyleO,"T",true);
var delStyleDoc=Doc(delStyleO);var deletedStyleP=delStyleDoc.Descendants(w+"p").FirstOrDefault(p=>string.Concat(p.Descendants(w+"delText").Select(t=>t.Value)).Contains("DELETED_STYLE"));
Check(deletedStyleP is not null,"styled deleted paragraph missing from Word export");
Check(deletedStyleP!.Element(w+"pPr")?.Element(w+"spacing")?.Attribute(w+"before")?.Value=="360","whole deleted paragraph lost original spacing");
Check(deletedStyleP.Element(w+"pPr")?.Element(w+"jc")?.Attribute(w+"val")?.Value=="right","whole deleted paragraph lost original alignment");
Check(deletedStyleP.Descendants(w+"del").Descendants(w+"rPr").Any(r=>r.Element(w+"b") is not null),"whole deleted paragraph lost original run formatting");
Console.WriteLine("PASS WORD WHOLE-DELETION STYLE PRESERVATION");


// 128. When both DOCX files reference the same named style, the output keeps B's style
// definition and docDefaults. Accepting all revisions must therefore render like revised B.
var styleDefA=Path.Combine(dir,"styleDefA.docx");var styleDefB=Path.Combine(dir,"styleDefB.docx");var styleDefO=Path.Combine(dir,"styleDefOut.docx");
Make(styleDefA,"<w:p><w:pPr><w:pStyle w:val='BodyCustom'/></w:pPr><w:r><w:t>Alpha OLD</w:t></w:r></w:p>");
Make(styleDefB,"<w:p><w:pPr><w:pStyle w:val='BodyCustom'/></w:pPr><w:r><w:t>Alpha NEW</w:t></w:r></w:p>");
AddWordXml(styleDefA,"word/styles.xml","<w:styles xmlns:w='"+W+"'><w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val='20'/></w:rPr></w:rPrDefault></w:docDefaults><w:style w:type='paragraph' w:styleId='BodyCustom'><w:name w:val='BodyCustom'/><w:rPr><w:b/></w:rPr></w:style></w:styles>");
AddWordXml(styleDefB,"word/styles.xml","<w:styles xmlns:w='"+W+"'><w:docDefaults><w:rPrDefault><w:rPr><w:sz w:val='40'/></w:rPr></w:rPrDefault></w:docDefaults><w:style w:type='paragraph' w:styleId='BodyCustom'><w:name w:val='BodyCustom'/><w:rPr><w:i/></w:rPr></w:style></w:styles>");
await eng.ExportWordAsync(styleDefA,styleDefB,styleDefO,"T",true);
using(var styleDefZip=ZipFile.OpenRead(styleDefO))
using(var styleDefStream=styleDefZip.GetEntry("word/styles.xml")!.Open())
{
    var styleDefXml=XDocument.Load(styleDefStream);var styleDefRoot=styleDefXml.Root!;
    var bodyCustom=styleDefRoot.Elements(w+"style").First(x=>x.Attribute(w+"styleId")?.Value=="BodyCustom");
    Check(bodyCustom.Descendants(w+"i").Any()&&!bodyCustom.Descendants(w+"b").Any(),"Word export overwrote revised named-style definition with original style");
    Check(styleDefRoot.Descendants(w+"rPrDefault").Descendants(w+"sz").FirstOrDefault()?.Attribute(w+"val")?.Value=="40","Word export overwrote revised docDefaults with original defaults");
}
Console.WriteLine("PASS WORD REVISED STYLE-DEFINITION BASELINE");


// 129. Legal/article mode divides the document at articles, but must still compare all content
// before the first article.  Preamble/title/date/intro text may not disappear from the result.
var fullLegalA=Path.Combine(dir,"fullLegalA.txt");var fullLegalB=Path.Combine(dir,"fullLegalB.txt");
File.WriteAllText(fullLegalA,string.Join("\n",new[]{
    "슈퍼캣 개인정보 처리방침",
    "2022-09-08",
    "주식회사 슈퍼캣은 이용자에게 최상의 서비스를 제공하고자 합니다.",
    "회사는 이용자의 개인정보를 안전하게 처리합니다.",
    "제1조(수집하는 이용자의 개인정보 항목 및 수집방법)",
    "① 회사는 서비스 제공에 필요한 정보를 수집합니다."
}),new UTF8Encoding(false));
File.WriteAllText(fullLegalB,string.Join("\n",new[]{
    "슈퍼캣 개인정보 처리방침",
    "2026-11-09",
    "주식회사 슈퍼캣은 이용자에게 최상의 서비스를 제공하고자 합니다.",
    "회사는 이용자의 개인정보를 안전하고 투명하게 처리합니다.",
    "제1조(수집하는 이용자의 개인정보 항목 및 수집방법)",
    "① 회사는 서비스 제공에 필요한 정보를 수집합니다."
}),new UTF8Encoding(false));
var fullLegal=await eng.CompareAsync(new[]{fullLegalA,fullLegalB},0,"legal",true,true);
Check(fullLegal.Rows.Count>=2,"legal comparison dropped the preamble block");
var preambleRow=fullLegal.Rows.FirstOrDefault(r=>r.Members.Any(m=>m?.Text.Contains("슈퍼캣 개인정보 처리방침")==true));
Check(preambleRow is not null,"legal comparison result does not contain document preamble/title");
Check(preambleRow!.Members[0]?.Kind=="block"&&preambleRow.Members[1]?.Kind=="block","preamble was not represented as a non-article block");
Check(preambleRow.Markers.Count>0&&preambleRow.DisplayMessages.Any(m=>m.Contains("2022-09-08")||m.Contains("2026-11-09")||m.Contains("투명")),"preamble changes were not compared");
var firstArticleRow=fullLegal.Rows.FirstOrDefault(r=>r.Members.Any(m=>m?.Kind=="article"));
Check(firstArticleRow is not null&&fullLegal.Rows.IndexOf(preambleRow)<fullLegal.Rows.IndexOf(firstArticleRow),"preamble was not kept before the first article");
Console.WriteLine("PASS LEGAL FULL-DOCUMENT PREAMBLE COMPARISON");

// 130. Chapter/part headings are real comparison units, not decoration.  This both catches
// hierarchy-title edits and lets the UI render chapter and article headings with different fills.
var hierarchyA=Path.Combine(dir,"hierarchyA.txt");var hierarchyB=Path.Combine(dir,"hierarchyB.txt");
File.WriteAllText(hierarchyA,"제1장 총칙\n제1조(목적)\n① 이 규정은 목적을 정합니다.",new UTF8Encoding(false));
File.WriteAllText(hierarchyB,"제1장 일반사항\n제1조(목적)\n① 이 규정은 목적을 정합니다.",new UTF8Encoding(false));
var hierarchyCmp=await eng.CompareAsync(new[]{hierarchyA,hierarchyB},0,"legal",true,true);
var hierarchyRow=hierarchyCmp.Rows.FirstOrDefault(r=>r.Members.Any(m=>m?.Kind=="section"));
Check(hierarchyRow is not null,"chapter heading was not emitted as a comparison unit");
Check(hierarchyRow!.Members[0]?.Header.Contains("제1장")==true&&hierarchyRow.Members[1]?.Header.Contains("제1장")==true,"chapter heading text missing");
Check(hierarchyRow.Markers.Count>0,"chapter title edit was not detected");
Check(!hierarchyCmp.Rows.Any(r=>r.SectionHeaders.Any(x=>!string.IsNullOrWhiteSpace(x))),"legacy section strip duplicated explicit chapter rows");
var hierarchyArticle=hierarchyCmp.Rows.FirstOrDefault(r=>r.Members.Any(m=>m?.Kind=="article"));
Check(hierarchyArticle is not null,"article row disappeared when chapter rows were emitted");
Console.WriteLine("PASS LEGAL HIERARCHY COMPARISON UNITS");


// 131. Regression for the privacy-policy table screenshot: A may carry paragraph shading and a
// table-style definition that differ from B. Export must not reapply those A properties to live B
// content. The revised table indentation/style and B header-cell presentation must survive intact.
var tableBaseA=Path.Combine(dir,"tableBaseA.docx");var tableBaseB=Path.Combine(dir,"tableBaseB.docx");var tableBaseO=Path.Combine(dir,"tableBaseOut.docx");
var tableA="<w:tbl><w:tblPr><w:tblStyle w:val='PrivacyTable'/><w:tblInd w:w='0' w:type='dxa'/></w:tblPr><w:tblGrid><w:gridCol w:w='1800'/><w:gridCol w:w='2100'/><w:gridCol w:w='2100'/></w:tblGrid><w:tr>"+
    "<w:tc><w:tcPr><w:shd w:fill='F2F2F2'/></w:tcPr><w:p><w:pPr><w:shd w:fill='FFFFFF'/><w:jc w:val='center'/></w:pPr><w:r><w:t>구분</w:t></w:r></w:p></w:tc>"+
    "<w:tc><w:tcPr><w:shd w:fill='F2F2F2'/></w:tcPr><w:p><w:pPr><w:shd w:fill='FFFFFF'/><w:jc w:val='center'/></w:pPr><w:r><w:t>책임자 및 담당부서</w:t></w:r></w:p></w:tc>"+
    "<w:tc><w:tcPr><w:shd w:fill='F2F2F2'/></w:tcPr><w:p><w:pPr><w:shd w:fill='FFFFFF'/><w:jc w:val='center'/></w:pPr><w:r><w:t>연락처 OLD</w:t></w:r></w:p></w:tc></w:tr></w:tbl>";
var tableB="<w:tbl><w:tblPr><w:tblStyle w:val='PrivacyTable'/><w:tblInd w:w='720' w:type='dxa'/></w:tblPr><w:tblGrid><w:gridCol w:w='1800'/><w:gridCol w:w='2100'/><w:gridCol w:w='2100'/></w:tblGrid><w:tr>"+
    "<w:tc><w:tcPr><w:shd w:fill='F2F2F2'/></w:tcPr><w:p><w:pPr><w:jc w:val='center'/></w:pPr><w:r><w:t>구분</w:t></w:r></w:p></w:tc>"+
    "<w:tc><w:tcPr><w:shd w:fill='F2F2F2'/></w:tcPr><w:p><w:pPr><w:jc w:val='center'/></w:pPr><w:r><w:t>책임자 및 담당부서</w:t></w:r></w:p></w:tc>"+
    "<w:tc><w:tcPr><w:shd w:fill='F2F2F2'/></w:tcPr><w:p><w:pPr><w:jc w:val='center'/></w:pPr><w:r><w:t>연락처 NEW</w:t></w:r></w:p></w:tc></w:tr></w:tbl>";
Make(tableBaseA,tableA);Make(tableBaseB,tableB);
AddWordXml(tableBaseA,"word/styles.xml","<w:styles xmlns:w='"+W+"'><w:style w:type='table' w:styleId='PrivacyTable'><w:name w:val='PrivacyTable'/><w:tblPr><w:tblInd w:w='0' w:type='dxa'/></w:tblPr></w:style></w:styles>");
AddWordXml(tableBaseB,"word/styles.xml","<w:styles xmlns:w='"+W+"'><w:style w:type='table' w:styleId='PrivacyTable'><w:name w:val='PrivacyTable'/><w:tblPr><w:tblInd w:w='720' w:type='dxa'/></w:tblPr></w:style></w:styles>");
await eng.ExportWordAsync(tableBaseA,tableBaseB,tableBaseO,"T",true);
var tableBaseDoc=Doc(tableBaseO);var tableBaseTbl=tableBaseDoc.Descendants(w+"tbl").First();
Check(tableBaseTbl.Element(w+"tblPr")?.Element(w+"tblInd")?.Attribute(w+"w")?.Value=="720","tracked export changed revised table indentation");
var tableBaseHeaderPs=tableBaseTbl.Descendants(w+"tr").First().Descendants(w+"p").ToList();
Check(tableBaseHeaderPs.All(p=>p.Element(w+"pPr")?.Element(w+"shd") is null),"tracked export reapplied original white paragraph shading over revised gray header cells");
using(var tableBaseZip=ZipFile.OpenRead(tableBaseO))
using(var tableBaseStyleStream=tableBaseZip.GetEntry("word/styles.xml")!.Open())
{
    var tableBaseStyles=XDocument.Load(tableBaseStyleStream);var privacyStyle=tableBaseStyles.Root!.Elements(w+"style").First(x=>x.Attribute(w+"styleId")?.Value=="PrivacyTable");
    Check(privacyStyle.Descendants(w+"tblInd").FirstOrDefault()?.Attribute(w+"w")?.Value=="720","tracked export overwrote revised table-style indentation with original style");
}
Console.WriteLine("PASS WORD REVISED TABLE/HEADER STYLE BASELINE");


// 132. Excel review export is a three-column 1280px review table. Legal content must be split
// into article/item rows instead of placing an entire article in one cell, and every row gets an
// explicit wrapped height so all text is visible without manual row resizing.
var excelStructA=Path.Combine(dir,"excelStructA.txt");var excelStructB=Path.Combine(dir,"excelStructB.txt");var excelStructO=Path.Combine(dir,"excelStruct.xlsx");
File.WriteAllText(excelStructA,string.Join("\n",new[]{
    "제1조(목적)",
    "① 회사는 다음 각 호의 업무를 수행합니다.",
    "1. 회원 관리 업무를 수행합니다.",
    "2. 결제 관리 업무를 수행합니다.",
    "② 회사는 이용자의 권리를 보호합니다."
}),new UTF8Encoding(false));
File.WriteAllText(excelStructB,string.Join("\n",new[]{
    "제1조(목적)",
    "① 회사는 다음 각 호의 업무를 안전하게 수행합니다.",
    "1. 회원 및 계정 관리 업무를 수행합니다.",
    "2. 결제 관리 업무를 수행합니다.",
    "② 회사는 이용자의 권리를 보호합니다."
}),new UTF8Encoding(false));
var excelStructCmp=await eng.CompareAsync(new[]{excelStructA,excelStructB},0,"legal",true,true);
await eng.ExportExcelAsync(excelStructCmp,excelStructO);
using(var excelStructZip=ZipFile.OpenRead(excelStructO))
{
    XNamespace sx="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    using var sheetStream=excelStructZip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
    var sheetXml=XDocument.Load(sheetStream);var sheetRoot=sheetXml.Root!;
    var cols=sheetRoot.Element(sx+"cols")!.Elements(sx+"col").ToList();
    Check(cols.Count==3,"Excel review sheet is not exactly three columns");
    var widths=cols.Select(c=>double.Parse(c.Attribute("width")!.Value,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    Check(Math.Abs(widths[0]-72.43)<.15&&Math.Abs(widths[1]-72.43)<.15&&Math.Abs(widths[2]-35.86)<.15,"Excel 1280px 40/40/20 widths changed: "+string.Join(",",widths.Select(x=>x.ToString("0.00"))));
    var rows=sheetRoot.Element(sx+"sheetData")!.Elements(sx+"row").Skip(1).ToList();
    string RowCellText(XElement row,int cellIndex)=>string.Concat(row.Elements(sx+"c").ElementAt(cellIndex).Descendants(sx+"t").Select(t=>t.Value));
    var leftTexts=rows.Select(r=>RowCellText(r,0)).ToList();
    Check(leftTexts.Any(x=>x.StartsWith("제1조(목적)",StringComparison.Ordinal)),"Excel article row missing");
    Check(leftTexts.Any(x=>x.StartsWith("① ",StringComparison.Ordinal)),"Excel 항 row missing");
    Check(leftTexts.Any(x=>x.StartsWith("1. ",StringComparison.Ordinal)),"Excel 호 1 row missing");
    Check(leftTexts.Any(x=>x.StartsWith("2. ",StringComparison.Ordinal)),"Excel 호 2 row missing");
    Check(leftTexts.Any(x=>x.StartsWith("② ",StringComparison.Ordinal)),"Excel second 항 row missing");
    Check(!leftTexts.Any(x=>x.Contains("① ")&&x.Contains("1. ")&&x.Contains("2. ")),"Excel still collapsed 항/호 into one article row");
    Check(rows.All(r=>r.Attribute("customHeight")?.Value=="1"&&double.TryParse(r.Attribute("ht")?.Value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var h)&&h>=21),"Excel data rows do not expose all wrapped text heights");
    Check(sheetRoot.Element(sx+"autoFilter")?.Attribute("ref")?.Value.StartsWith("A1:C",StringComparison.Ordinal)==true,"Excel filter range is not A:C");
    var itemRow=rows.First(r=>RowCellText(r,0).StartsWith("① ",StringComparison.Ordinal));
    Check(itemRow.Elements(sx+"c").Take(3).All(c=>c.Attribute("s")?.Value=="4"),"Excel body/item cells are not using the dedicated wrapped style");
}
using(var excelStructStyleZip=ZipFile.OpenRead(excelStructO))
using(var styleStream=excelStructStyleZip.GetEntry("xl/styles.xml")!.Open())
{
    XNamespace sx="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    var styles=XDocument.Load(styleStream);
    var xfs=styles.Root!.Element(sx+"cellXfs")!.Elements(sx+"xf").ToList();
    Check(xfs.Count>=5,"Excel wrapped body style missing");
    var bodyAlignment=xfs[4].Element(sx+"alignment");
    Check(bodyAlignment?.Attribute("wrapText")?.Value=="1"&&bodyAlignment?.Attribute("vertical")?.Value=="top",
        "Excel body style does not force wrapped/top-aligned text");
}
using(var excelStructDoc=SpreadsheetDocument.Open(excelStructO,false))
{
    var errors=new OpenXmlValidator().Validate(excelStructDoc).ToList();
    Check(errors.Count==0,"structured Excel package invalid: "+string.Join(" | ",errors.Take(8).Select(e=>e.Description)));
}
Console.WriteLine("PASS EXCEL STRUCTURAL ROWS + 1280 40/40/20 LAYOUT");

// 133. Three-document Excel comparison keeps A/B/C together on one review sheet. The 80%
// document area is divided equally across the three documents and the remaining 20% is Changes.
var excel3A=Path.Combine(dir,"excel3A.txt");var excel3B=Path.Combine(dir,"excel3B.txt");var excel3C=Path.Combine(dir,"excel3C.txt");var excel3O=Path.Combine(dir,"excel3.xlsx");
File.WriteAllText(excel3A,"제1조(목적)\n① Alpha\n1. A-one",new UTF8Encoding(false));
File.WriteAllText(excel3B,"제1조(목적)\n① Beta\n1. B-one",new UTF8Encoding(false));
File.WriteAllText(excel3C,"제1조(목적)\n① Gamma\n1. C-one",new UTF8Encoding(false));
var excel3Cmp=await eng.CompareAsync(new[]{excel3A,excel3B,excel3C},0,"legal",true,true);
await eng.ExportExcelAsync(excel3Cmp,excel3O);
using(var excel3Zip=ZipFile.OpenRead(excel3O))
{
    XNamespace sx="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    using var sheetStream=excel3Zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
    var sheetXml=XDocument.Load(sheetStream);var root=sheetXml.Root!;
    var cols=root.Element(sx+"cols")!.Elements(sx+"col").ToList();
    Check(cols.Count==4,"three-way Excel is not A/B/C/Changes four-column layout");
    var widths=cols.Select(c=>double.Parse(c.Attribute("width")!.Value,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    Check(Math.Abs(widths[0]-48.00)<.15&&Math.Abs(widths[1]-48.00)<.15&&Math.Abs(widths[2]-48.14)<.15&&Math.Abs(widths[3]-35.86)<.15,
        "three-way Excel 80/3 + 20 widths changed: "+string.Join(",",widths.Select(x=>x.ToString("0.00"))));
    var firstRow=root.Element(sx+"sheetData")!.Elements(sx+"row").First();
    var headers=firstRow.Elements(sx+"c").Select(c=>string.Concat(c.Descendants(sx+"t").Select(t=>t.Value))).ToList();
    Check(headers.Count==4&&headers[0].StartsWith("A ·")&&headers[1].StartsWith("B ·")&&headers[2].StartsWith("C ·")&&headers[3]=="변경사항",
        "three-way Excel headers are not A/B/C/Changes: "+string.Join(" | ",headers));
    Check(root.Element(sx+"autoFilter")?.Attribute("ref")?.Value.StartsWith("A1:D",StringComparison.Ordinal)==true,"three-way Excel filter range is not A:D");
    var dataRows=root.Element(sx+"sheetData")!.Elements(sx+"row").Skip(1).ToList();
    Check(dataRows.SelectMany(r=>r.Elements(sx+"c")).Where(c=>c.Attribute("s")?.Value=="4").Any(),"three-way Excel body cells are not using wrapped style");
}
using(var excel3Doc=SpreadsheetDocument.Open(excel3O,false))
{
    Check(excel3Doc.WorkbookPart!.WorksheetParts.Count()==1,"three-way Excel should use one combined comparison sheet");
    var errors=new OpenXmlValidator().Validate(excel3Doc).ToList();
    Check(errors.Count==0,"three-way Excel package invalid: "+string.Join(" | ",errors.Take(8).Select(e=>e.Description)));
}
Console.WriteLine("PASS EXCEL THREE-WAY 80/3 + 20 LAYOUT");


// 134. Version history formatting inspection: identical text with a direct font-size change
// must be reported as formatting, not disappear because the content text is unchanged.
var vhFmtA=Path.Combine(dir,"vhFmtA.docx");var vhFmtB=Path.Combine(dir,"vhFmtB.docx");
Make(vhFmtA,"<w:p><w:r><w:rPr><w:sz w:val='20'/></w:rPr><w:t>Version text</w:t></w:r></w:p>");
Make(vhFmtB,"<w:p><w:r><w:rPr><w:sz w:val='28'/></w:rPr><w:t>Version text</w:t></w:r></w:p>");
var vhFmt=VersionFormattingInspector.Compare(vhFmtA,vhFmtB);
Check(vhFmt.Any(x=>x.Category=="문자서식"&&x.Detail.Contains("10pt")&&x.Detail.Contains("14pt")&&x.AnchorText=="Version text"),"version history did not detect/anchor font-size change: "+string.Join(" | ",vhFmt.Select(x=>x.Category+":"+x.Detail)));
Console.WriteLine("PASS VERSION HISTORY FONT-SIZE DIFF");

// 135. Version history formatting inspection: table/cell presentation changes are first-class
// version changes even when visible cell text is identical.
var vhTblA=Path.Combine(dir,"vhTblA.docx");var vhTblB=Path.Combine(dir,"vhTblB.docx");
Make(vhTblA,"<w:tbl><w:tblPr><w:tblInd w:w='0' w:type='dxa'/></w:tblPr><w:tblGrid><w:gridCol w:w='2400'/></w:tblGrid><w:tr><w:tc><w:tcPr><w:shd w:fill='FFFFFF'/></w:tcPr>"+P("Same cell")+"</w:tc></w:tr></w:tbl>");
Make(vhTblB,"<w:tbl><w:tblPr><w:tblInd w:w='720' w:type='dxa'/></w:tblPr><w:tblGrid><w:gridCol w:w='2400'/></w:tblGrid><w:tr><w:tc><w:tcPr><w:shd w:fill='F2F2F2'/></w:tcPr>"+P("Same cell")+"</w:tc></w:tr></w:tbl>");
var vhTbl=VersionFormattingInspector.Compare(vhTblA,vhTblB);
Check(vhTbl.Any(x=>x.Category=="표"&&x.Title.Contains("표 1")&&(x.Detail.Contains("표 속성")||x.Detail.Contains("셀 서식"))),"version history did not detect table formatting change: "+string.Join(" | ",vhTbl.Select(x=>x.Category+":"+x.Detail)));
Console.WriteLine("PASS VERSION HISTORY TABLE FORMAT DIFF");

// 136. Version history formatting inspection: named Word style-definition edits are retained
// as their own audit entry rather than being mistaken for a content change.
var vhStyleA=Path.Combine(dir,"vhStyleA.docx");var vhStyleB=Path.Combine(dir,"vhStyleB.docx");
Make(vhStyleA,P("Styled text"));Make(vhStyleB,P("Styled text"));
AddWordXml(vhStyleA,"word/styles.xml","<w:styles xmlns:w='"+W+"'><w:style w:type='paragraph' w:styleId='BodyCustom'><w:name w:val='BodyCustom'/><w:rPr><w:sz w:val='20'/></w:rPr></w:style></w:styles>");
AddWordXml(vhStyleB,"word/styles.xml","<w:styles xmlns:w='"+W+"'><w:style w:type='paragraph' w:styleId='BodyCustom'><w:name w:val='BodyCustom'/><w:rPr><w:sz w:val='24'/></w:rPr></w:style></w:styles>");
var vhStyle=VersionFormattingInspector.Compare(vhStyleA,vhStyleB);
Check(vhStyle.Any(x=>x.Category=="스타일"&&x.Detail.Contains("BodyCustom")&&x.Detail.Contains("10pt")&&x.Detail.Contains("12pt")),"version history did not describe named-style definition change");
Console.WriteLine("PASS VERSION HISTORY NAMED-STYLE DIFF");

// 136b. Version-history style descriptions use human-readable Word units and style names.
var vhReadableA=Path.Combine(dir,"vhReadableA.docx");var vhReadableB=Path.Combine(dir,"vhReadableB.docx");
Make(vhReadableA,"<w:p><w:pPr><w:pStyle w:val='BodyCustom'/></w:pPr><w:r><w:t>Readable style</w:t></w:r></w:p>");
Make(vhReadableB,"<w:p><w:pPr><w:pStyle w:val='BodyCustom'/></w:pPr><w:r><w:t>Readable style</w:t></w:r></w:p>");
AddWordXml(vhReadableA,"word/styles.xml","<w:styles xmlns:w='"+W+"'><w:style w:type='paragraph' w:styleId='a'><w:name w:val='기본 본문'/></w:style><w:style w:type='paragraph' w:styleId='BodyCustom'><w:name w:val='본문'/><w:basedOn w:val='a'/><w:pPr><w:spacing w:line='240'/></w:pPr><w:rPr><w:color w:val='000000'/></w:rPr></w:style></w:styles>");
AddWordXml(vhReadableB,"word/styles.xml","<w:styles xmlns:w='"+W+"'><w:style w:type='paragraph' w:styleId='a'><w:name w:val='기본 본문'/></w:style><w:style w:type='paragraph' w:styleId='BodyCustom'><w:name w:val='본문'/><w:basedOn w:val='a'/><w:pPr><w:spacing w:line='384'/></w:pPr><w:rPr><w:color w:val='434343'/></w:rPr></w:style></w:styles>");
var vhReadable=VersionFormattingInspector.Compare(vhReadableA,vhReadableB);
var readableDetail=vhReadable.First(x=>x.Category=="스타일").Detail;
Check(readableDetail.Contains("줄간격=1.6줄")&&readableDetail.Contains("색상=#434343")&&readableDetail.Contains("기반 스타일=기본 본문 (a)"),
    "version history style description is not human-readable: "+readableDetail);
Console.WriteLine("PASS VERSION HISTORY READABLE STYLE VALUES");

// 137. Version-history preview resolves paragraph style inheritance into visible font/alignment.
var vhPreview=Path.Combine(dir,"vhPreview.docx");
Make(vhPreview,"<w:p><w:pPr><w:pStyle w:val='BodyCustom'/></w:pPr><w:r><w:t>Styled preview</w:t></w:r></w:p>");
AddWordXml(vhPreview,"word/styles.xml","<w:styles xmlns:w='"+W+"'><w:style w:type='paragraph' w:styleId='BaseCustom'><w:rPr><w:rFonts w:ascii='Arial' w:eastAsia='Arial'/><w:sz w:val='20'/></w:rPr></w:style><w:style w:type='paragraph' w:styleId='BodyCustom'><w:basedOn w:val='BaseCustom'/><w:pPr><w:jc w:val='center'/></w:pPr><w:rPr><w:b/><w:sz w:val='30'/></w:rPr></w:style></w:styles>");
var vhPreviewMap=VersionDocumentStyleMap.Load(vhPreview);var vhParagraph=vhPreviewMap?.Take("Styled preview");
Check(vhParagraph is not null&&vhParagraph.Alignment==Avalonia.Media.TextAlignment.Center,"version preview paragraph alignment style missing");
var vhRun=vhParagraph!.Runs.Single().Style;
Check(vhRun.Bold&&vhRun.FontFamily=="Arial"&&vhRun.FontSize is double vhSize&&Math.Abs(vhSize-20.0)<.05,"version preview did not resolve inherited font/size/bold style");
Console.WriteLine("PASS VERSION HISTORY STYLE-AWARE PREVIEW");

// 138. Version-history Before/After selection is independently toggleable.
var pair=VersionSelectionPolicy.Toggle(-1,-1,0); Check(pair.Before==0&&pair.After==-1&&pair.Changed,"first version was not assigned as Before");
pair=VersionSelectionPolicy.Toggle(pair.Before,pair.After,1); Check(pair.Before==0&&pair.After==1&&pair.Changed,"second version was not assigned as After");
pair=VersionSelectionPolicy.Toggle(pair.Before,pair.After,1); Check(pair.Before==0&&pair.After==-1&&pair.Changed,"clicking After again did not clear After");
pair=VersionSelectionPolicy.Toggle(pair.Before,pair.After,2); Check(pair.Before==0&&pair.After==2&&pair.Changed,"cleared After role could not be reassigned");
pair=VersionSelectionPolicy.Toggle(pair.Before,pair.After,0); Check(pair.Before==-1&&pair.After==2&&pair.Changed,"clicking Before again did not clear Before");
pair=VersionSelectionPolicy.Toggle(pair.Before,pair.After,1); Check(pair.Before==1&&pair.After==2&&pair.Changed,"cleared Before role could not be reassigned");
var unchanged=VersionSelectionPolicy.Toggle(pair.Before,pair.After,3); Check(!unchanged.Changed&&unchanged.Before==1&&unchanged.After==2,"third unassigned file replaced a full pair without explicit release");
Console.WriteLine("PASS VERSION HISTORY BEFORE/AFTER TOGGLE");

// 139. Version-history file import accepts dropped DOCX/TXT paths, preserves order, and ignores duplicates/unsupported files.
var dropTxt=Path.Combine(dir,"drop-v01.txt"); var dropDocx=Path.Combine(dir,"drop-v02.docx"); var dropPdf=Path.Combine(dir,"drop-ignore.pdf");
File.WriteAllText(dropTxt,"v0.1",new UTF8Encoding(false)); Make(dropDocx,P("v0.2")); File.WriteAllText(dropPdf,"not supported",new UTF8Encoding(false));
var dropProject=new VersionHistoryProjectVm();
var dropResult=VersionFileImportPolicy.AddPaths(dropProject,new[]{dropTxt,dropDocx,dropTxt,dropPdf,Path.Combine(dir,"missing.docx")});
Check(dropResult.Added==2&&dropResult.Duplicates==1&&dropResult.Unsupported==1&&dropResult.Missing==1,"version drop import counts changed");
Check(dropProject.Versions.Count==2&&Path.GetFileName(dropProject.Versions[0].Path)=="drop-v01.txt"&&Path.GetFileName(dropProject.Versions[1].Path)=="drop-v02.docx","version drop import order changed");
Check(dropProject.Versions.All(x=>x.Sha256.Length==64),"version drop import did not hash added files");
Console.WriteLine("PASS VERSION HISTORY FILE DROP IMPORT");

// 140. Version-history DOCX preview reconstructs Word table rows/cells instead of leaving them as pipe-delimited text.
var vhTablePreview=Path.Combine(dir,"vhTablePreview.docx");
Make(vhTablePreview,"<w:tbl><w:tblGrid><w:gridCol w:w='1800'/><w:gridCol w:w='2600'/><w:gridCol w:w='2200'/></w:tblGrid>"+
    "<w:tr><w:tc><w:tcPr><w:shd w:fill='E7E6E6'/></w:tcPr>"+P("구분")+"</w:tc><w:tc>"+P("담당부서")+"</w:tc><w:tc>"+P("연락처")+"</w:tc></w:tr>"+
    "<w:tr><w:tc>"+P("책임자")+"</w:tc><w:tc>"+P("플랫폼팀")+"</w:tc><w:tc>"+P("02-0000-0000")+"</w:tc></w:tr></w:tbl>");
var vhTableMap=VersionDocxTableMap.Load(vhTablePreview);
Check(vhTableMap is not null,"version table map was not created");
var vhTableRow=vhTableMap!.Take("구분 | 담당부서 | 연락처");
Check(vhTableRow is not null&&vhTableRow.Cells.Count==3&&vhTableRow.ColumnWidths.SequenceEqual(new[]{1800d,2600d,2200d}),"version table preview did not preserve three-cell grid widths");
Check(vhTableRow!.Cells[0].Shading=="#E7E6E6"&&vhTableRow.Cells[1].Text=="담당부서","version table preview did not preserve cell content/shading");
Console.WriteLine("PASS VERSION HISTORY TABLE RENDER MODEL");

// 141. Version-history markers are globally numbered across rows. The core comparison engine
// numbers markers within each row for the quick-compare UI, but the single-document version
// preview needs one monotonically increasing sequence for the whole document.
var numberingResult=new ComparisonResultVm
{
    Rows=new List<ComparisonRowVm>
    {
        new(){Id=1,Markers=new List<MarkerVm>{new(){Num=1,Action="변경",Message="first"},new(){Num=2,Action="변경",Message="second"}}},
        new(){Id=2,Markers=new List<MarkerVm>{new(){Num=1,Action="변경",Message="third"}}},
        new(){Id=3,Markers=new List<MarkerVm>{new(){Num=1,Action="추가",Message="fourth"},new(){Num=2,Action="삭제",Message="fifth"}}}
    }
};
VersionHistoryMarkerNumbering.ReindexGlobally(numberingResult);
var globalNums=numberingResult.Rows.SelectMany(x=>x.Markers).Select(x=>x.Num).ToArray();
Check(globalNums.SequenceEqual(new[]{1,2,3,4,5}),"version-history marker numbering restarted per row: "+string.Join(",",globalNums));
Console.WriteLine("PASS VERSION HISTORY GLOBAL MARKER NUMBERING");

// 142. Every version-history change category receives one global review number, including
// structural and formatting changes that do not originate from text markers.
var allChangeNumbers=VersionHistoryChangeNumbering.EnsureNumbered(new[]
{
    new VersionChangeVm{Category="내용",Title="변경",MarkerNumber=1,RowId=10},
    new VersionChangeVm{Category="내용",Title="추가",MarkerNumber=2,RowId=11},
    new VersionChangeVm{Category="구조",Title="구조 변경",RowId=12},
    new VersionChangeVm{Category="문자서식",Title="글자 서식 변경",AnchorText="Styled"},
    new VersionChangeVm{Category="스타일",Title="Word 스타일 정의 변경"}
});
Check(allChangeNumbers.Select(x=>x.MarkerNumber).SequenceEqual(new int?[]{1,2,3,4,5}),
    "supplemental version-history changes were not globally numbered: "+string.Join(",",allChangeNumbers.Select(x=>x.MarkerNumber)));
Check(allChangeNumbers[2].Category=="구조"&&allChangeNumbers[3].Category=="문자서식"&&allChangeNumbers[4].Category=="스타일",
    "version-history numbering changed supplemental change ordering/categories");
Console.WriteLine("PASS VERSION HISTORY ALL CHANGE TYPES NUMBERED");

// 143. Version History uses one top-to-bottom sequence across content and structural changes.
var orderedResult=new ComparisonResultVm
{
    Rows=new List<ComparisonRowVm>
    {
        new()
        {
            Id=10, Changed=true,
            Members=new List<MemberVm?>{new(){Text="old title"},new(){Text="new title"}},
            Markers=new List<MarkerVm>{new(){Num=1,Action="변경",Message="title",TargetDoc=1,CharStart=2,CharEnd=5,Endpoints=new(){new(){TargetDoc=1,CharStart=2,CharEnd=5}}}}
        },
        new()
        {
            Id=20, Changed=true,
            Members=new List<MemberVm?>{new(){Text="old article"},new(){Header="Article 1",Body="changed body",Text="Article 1\nchanged body"}},
            DisplayMessages=new List<string>{"상태: 조 구조변경","항/호 구조 변경: 1., 2. → ①, ②"},
            Markers=new List<MarkerVm>{new(){Num=1,Action="변경",Message="body",TargetDoc=1,CharStart=20,CharEnd=25,Endpoints=new(){new(){TargetDoc=1,CharStart=20,CharEnd=25}}}}
        },
        new()
        {
            Id=30, Changed=true,
            Members=new List<MemberVm?>{new(){Text="deleted text"},null},
            Markers=new List<MarkerVm>{new(){Num=1,Action="삭제",Message="deleted",TargetDoc=0,CharStart=0,CharEnd=12}}
        }
    }
};
var orderedChanges=VersionHistoryChangeSequencer.Sequence(orderedResult,Array.Empty<VersionChangeVm>(),1);
Check(orderedChanges.Select(x=>x.MarkerNumber).SequenceEqual(new int?[]{1,2,3,4}),
    "document-order version numbering is not contiguous: "+string.Join(",",orderedChanges.Select(x=>x.MarkerNumber)));
Check(orderedChanges[0].Category=="내용"&&orderedChanges[1].Category=="구조"&&orderedChanges[1].RowId==20&&orderedChanges[2].Category=="내용"&&orderedChanges[3].Category=="내용",
    "structural change was appended after document content instead of staying at its row position");
Check(orderedResult.Rows[0].Markers.Single().Num==1&&orderedResult.Rows[1].Markers.Single().Num==3&&orderedResult.Rows[2].Markers.Single().Num==4,
    "rendered marker numbers did not receive the unified document-order sequence");
Console.WriteLine("PASS VERSION HISTORY DOCUMENT-ORDER CHANGE SEQUENCE");

// 144. Anchored formatting/style changes are inserted at their document location instead of being appended globally.
var anchoredResult=new ComparisonResultVm
{
    Rows=new List<ComparisonRowVm>
    {
        new(){Id=1,Members=new List<MemberVm?>{new(){Text="Heading"},new(){Text="Heading"}}},
        new(){Id=2,Members=new List<MemberVm?>{new(){Text="Body paragraph"},new(){Text="Body paragraph"}},Markers=new List<MarkerVm>{new(){Num=1,Action="변경",Message="text",TargetDoc=1,CharStart=8,CharEnd=12,Endpoints=new(){new(){TargetDoc=1,CharStart=8,CharEnd=12}}}}}
    }
};
var anchoredFormatting=new List<VersionChangeVm>{new(){Category="문자서식",Title="글자 서식 변경",AnchorText="Body paragraph",Detail="size"}};
var anchoredChanges=VersionHistoryChangeSequencer.Sequence(anchoredResult,anchoredFormatting,1);
Check(anchoredChanges.Count==2&&anchoredChanges[0].Category=="문자서식"&&anchoredChanges[0].MarkerNumber==1&&anchoredChanges[1].Category=="내용"&&anchoredChanges[1].MarkerNumber==2,
    "anchored formatting change was not sequenced at its document position");
Console.WriteLine("PASS VERSION HISTORY ANCHORED FORMAT ORDER");

// 145. Version-history preview line height must never be smaller than the tallest visible run.
var clippedParagraph=new VersionParagraphVisualStyle
{
    Text="큰 제목",
    LineHeight=18,
    DefaultRun=new VersionRunVisualStyle{FontSize=14},
    Runs=new List<VersionRunVisualSpan>
    {
        new(){Start=0,End=3,Style=new VersionRunVisualStyle{FontSize=32}}
    }
};
var safeHeight=VersionPreviewLayoutPolicy.ResolveLineHeight(clippedParagraph,13);
Check(safeHeight>=48,"version preview line-height floor did not protect large glyphs: "+safeHeight);
var roomyParagraph=new VersionParagraphVisualStyle{Text="본문",LineHeight=48,DefaultRun=new VersionRunVisualStyle{FontSize=13}};
Check(Math.Abs(VersionPreviewLayoutPolicy.ResolveLineHeight(roomyParagraph,13)-48)<.01,"existing roomy Word line height was not preserved");
Check(VersionPreviewLayoutPolicy.ResolveLineHeight(null,13)>=20,"default preview line height is still too tight for mixed Korean/Latin text");
Console.WriteLine("PASS VERSION HISTORY SAFE LINE HEIGHT");

// 146. A plain article/content status is not a structural change; only concrete hierarchy/move/add/delete changes are.
var plainStatusResult=new ComparisonResultVm
{
    Rows=new List<ComparisonRowVm>
    {
        new()
        {
            Id=100,Changed=true,
            Members=new List<MemberVm?>{new(){Text="old"},new(){Text="new"}},
            DisplayMessages=new List<string>{"A↔B · 상태: 조 변경"},
            Markers=new List<MarkerVm>{new(){Num=1,Action="변경",Message="old → new",TargetDoc=1,CharStart=0,CharEnd=3,Endpoints=new(){new(){TargetDoc=1,CharStart=0,CharEnd=3}}}}
        },
        new()
        {
            Id=101,Changed=true,
            Members=new List<MemberVm?>{new(){Text="1. old"},new(){Text="① new"}},
            DisplayMessages=new List<string>{"A↔B · 상태: 조 구조변경","A↔B · 항/호 구조 변경: 1. → ①"}
        }
    }
};
var plainStatusChanges=VersionHistoryChangeSequencer.Sequence(plainStatusResult,Array.Empty<VersionChangeVm>(),1);
Check(plainStatusChanges.Count(x=>x.Category=="구조") == 1,
    "plain article content status was incorrectly emitted as a structural change: "+string.Join(" | ",plainStatusChanges.Select(x=>x.Category+":"+x.Title+":"+x.Detail)));
var structuralOnly=plainStatusChanges.Single(x=>x.Category=="구조");
Check(structuralOnly.Title=="항/호 구조 변경"&&structuralOnly.Detail.Contains("1. → ①"),
    "concrete structural change was not described clearly");
Console.WriteLine("PASS VERSION HISTORY CONCRETE STRUCTURE LABELS");

// 147. Saved projects persist immutable blobs instead of depending on the mutable source path.
var snapshotPath=Path.Combine(dir,"immutable-version.txt");
File.WriteAllText(snapshotPath,"v1: alpha",new UTF8Encoding(false));
var transientStore=VersionSnapshotStore.CreateTemporary();
var snapshotProject=new VersionHistoryProjectVm();
var firstSnapshot=VersionFileImportPolicy.AddPaths(snapshotProject,new[]{snapshotPath},transientStore);
Check(firstSnapshot.Added==1&&snapshotProject.Versions[0].SnapshotBlobId?.Length==64,"new imports must capture a snapshot");
File.WriteAllText(snapshotPath,"v2: beta",new UTF8Encoding(false));
var secondSnapshot=VersionFileImportPolicy.AddPaths(snapshotProject,new[]{snapshotPath},transientStore);
Check(secondSnapshot.Added==1&&snapshotProject.Versions.Count==2,"same source path with new bytes must be a new version");
var identicalSnapshot=VersionFileImportPolicy.AddPaths(snapshotProject,new[]{snapshotPath},transientStore);
Check(identicalSnapshot.Added==0&&identicalSnapshot.Duplicates==1,"same source path/content must deduplicate");
var savedPath=Path.Combine(dir,"project.dcv.json");
var persisted=await VersionHistoryProjectStore.SaveAsync(snapshotProject,transientStore,savedPath);
var savedStore=VersionSnapshotStore.ForProject(savedPath);
File.Delete(snapshotPath);
Check(File.ReadAllText(savedStore.Resolve(persisted.Versions[0]))=="v1: alpha"&&
      File.ReadAllText(savedStore.Resolve(persisted.Versions[1]))=="v2: beta",
      "past versions must survive source replacement/deletion");
var reopened=System.Text.Json.JsonSerializer.Deserialize<VersionHistoryProjectVm>(File.ReadAllText(savedPath))!;
Check(reopened.Versions.Count==2&&File.ReadAllText(savedStore.Resolve(reopened.Versions[0]))=="v1: alpha",
      "saved project does not reopen immutable content");
var snapshotCompare=await eng.CompareAsync(new[]{savedStore.Resolve(reopened.Versions[0]),savedStore.Resolve(reopened.Versions[1])},0,"general",false,true);
Check(snapshotCompare.Rows.Any(row=>row.Changed),"historical snapshots must remain comparable after original deletion");
var maliciousVersion=new VersionFileVm{Path=snapshotPath,SnapshotBlobId="../not-a-blob",Sha256="../not-a-blob"};
var unsafeIdRejected=false;
try { savedStore.Resolve(maliciousVersion); } catch(InvalidDataException) { unsafeIdRejected=true; }
Check(unsafeIdRejected,"malformed snapshot IDs must not access arbitrary filesystem paths");
var savedAgain=await VersionHistoryProjectStore.SaveAsync(reopened,savedStore,savedPath);
Check(File.ReadAllText(savedStore.Resolve(savedAgain.Versions[1]))=="v2: beta",
      "re-saving an already saved project must preserve all version blobs");
var firstStored=savedStore.Resolve(reopened.Versions[0]);
File.WriteAllText(firstStored,"tampered");
var corruptedDetected=false;
try { savedStore.Resolve(reopened.Versions[0]); } catch(InvalidDataException) { corruptedDetected=true; }
Check(corruptedDetected,"corrupt snapshot content must not be shown as valid history");
Console.WriteLine("PASS IMMUTABLE VERSION SNAPSHOTS AND CORRUPTION DETECTION");

// 148. Existing path-only .dcv.json projects migrate on save without falsely claiming missing content.
var legacyPath=Path.Combine(dir,"legacy.txt");File.WriteAllText(legacyPath,"legacy body");
var missingLegacy=Path.Combine(dir,"missing-legacy.txt");
var legacyProject=new VersionHistoryProjectVm{Versions=new(){new(){Path=legacyPath},new(){Path=missingLegacy}}};
var legacySavedPath=Path.Combine(dir,"legacy.dcv.json");
var migrated=await VersionHistoryProjectStore.SaveAsync(legacyProject,VersionSnapshotStore.CreateTemporary(),legacySavedPath);
Check(migrated.Versions[0].SnapshotBlobId?.Length==64&&
      File.ReadAllText(VersionSnapshotStore.ForProject(legacySavedPath).Resolve(migrated.Versions[0]))=="legacy body",
      "legacy existing file must be migrated to immutable content");
Check(migrated.Versions[1].SnapshotBlobId is null,"missing legacy source must never be marked as preserved");
Console.WriteLine("PASS LEGACY VERSION PROJECT MIGRATION");

// 156. Never use a modified source file to fake a previously recorded legacy version.
var legacyChanged=Path.Combine(dir,"legacy-changed.txt");
File.WriteAllText(legacyChanged,"the original");
var legacyExpectedHash=VersionSnapshotStore.HashFile(legacyChanged);
File.WriteAllText(legacyChanged,"newer content at the same filename");
var legacyChangedProject=new VersionHistoryProjectVm
{
    Versions=new(){new(){Path=legacyChanged,Sha256=legacyExpectedHash}}
};
var legacyChangedSavedPath=Path.Combine(dir,"legacy-changed.dcv.json");
var legacyChangedSaved=await VersionHistoryProjectStore.SaveAsync(
    legacyChangedProject,VersionSnapshotStore.CreateTemporary(),legacyChangedSavedPath);
Check(legacyChangedSaved.Versions[0].SnapshotBlobId is null &&
      legacyChangedSaved.Versions[0].Sha256==legacyExpectedHash,
      "changed legacy file was incorrectly recorded as the original historical revision");
var changedLegacyReadRejected=false;
try { VersionSnapshotStore.ForProject(legacyChangedSavedPath).Resolve(legacyChangedSaved.Versions[0]); }
catch(InvalidDataException) { changedLegacyReadRejected=true; }
Check(changedLegacyReadRejected,
      "changed original must not be displayed as an intact legacy historical revision");
var legacyChangedReopened=System.Text.Json.JsonSerializer.Deserialize<VersionHistoryProjectVm>(
    File.ReadAllText(legacyChangedSavedPath))!;
Check(legacyChangedReopened.Versions[0].SnapshotBlobId is null &&
      legacyChangedReopened.Versions[0].Sha256==legacyExpectedHash,
      "saving a mismatched historical version must not overwrite its original checksum");
File.WriteAllText(legacyChanged,"the original");
var legacyRecovered=await VersionHistoryProjectStore.SaveAsync(
    legacyChangedReopened,VersionSnapshotStore.CreateTemporary(),legacyChangedSavedPath);
Check(legacyRecovered.Versions[0].SnapshotBlobId==legacyExpectedHash &&
      File.ReadAllText(VersionSnapshotStore.ForProject(legacyChangedSavedPath).Resolve(legacyRecovered.Versions[0]))=="the original",
      "restoring the original source bytes must make an intact historical revision recoverable");
Console.WriteLine("PASS LEGACY VERSION HASH-CONSTRAINED RECOVERY");



// 149. Shared change-event identity is independent of display review numbers.
var fromLocation=new UnifiedChangeLocation("version-A","src/test.rs",UnifiedLocationKind.CodeNode,"fn:x",10,30);
var toLocation=new UnifiedChangeLocation("version-B","src/test.rs",UnifiedLocationKind.CodeNode,"fn:y",10,33);
var eventA=UnifiedChangeEvents.Create(UnifiedChangeKind.Rename,fromLocation,toLocation,"function renamed","AST+body match",0,.95);
var eventB=UnifiedChangeEvents.Create(UnifiedChangeKind.Rename,fromLocation,toLocation,"function renamed","AST+body match",0,.95);
var eventC=UnifiedChangeEvents.Create(UnifiedChangeKind.Rename,fromLocation,toLocation,"function renamed","AST+body match",1,.95);
Check(eventA.EventId==eventB.EventId&&eventA.EventId!=eventC.EventId,
      "unified change IDs must be deterministic and distinguish duplicate changes");
Console.WriteLine("PASS UNIFIED CHANGE EVENT IDENTITY");

// 150. Code file versions are preserved as immutable content and compared line-by-line.
var codePath=Path.Combine(dir,"code-version.rs");
File.WriteAllText(codePath,"fn greet() {\n  println!(\"안녕\");\n}\n");
var codeStore=VersionSnapshotStore.CreateTemporary();
var codeProject=new VersionHistoryProjectVm();
Check(VersionFileImportPolicy.IsCodeFile(codePath)&&VersionFileImportPolicy.IsSupported(codePath),
      "Rust source files must be valid version imports");
Check(VersionFileImportPolicy.AddPaths(codeProject,new[]{codePath},codeStore).Added==1,"source code version import failed");
File.WriteAllText(codePath,"fn greet() {\n  println!(\"안녕하세요\");\n}\n");
Check(VersionFileImportPolicy.AddPaths(codeProject,new[]{codePath},codeStore).Added==1,"new source code content at same path not versioned");
var codeProjectPath=Path.Combine(dir,"source-code.dcv.json");
var savedCodeProject=await VersionHistoryProjectStore.SaveAsync(codeProject,codeStore,codeProjectPath);
File.Delete(codePath);
var codeHistory=VersionSnapshotStore.ForProject(codeProjectPath);
var changedCode=CodeVersionComparer.Compare(
    CodeVersionComparer.ReadCodeFile(codeHistory.Resolve(savedCodeProject.Versions[0])),
    CodeVersionComparer.ReadCodeFile(codeHistory.Resolve(savedCodeProject.Versions[1])));
Check(changedCode.Changes.Count==1&&changedCode.Changes[0].Kind=="교체"&&
      changedCode.Changes[0].BeforeStart==1&&changedCode.Changes[0].AfterStart==1,
      "source code change must map to the correct line after deleting the original");
Check(CodeVersionComparer.Compare("fn a() {}\n","fn a() {}\n").Changes.Count==0,
      "identical code lines should not produce changes");
Check(CodeVersionComparer.Compare("a\n","a\nb\n").Changes.Single().Kind=="추가",
      "appended code line should be an insertion");
Check(CodeVersionComparer.Compare("a\nb\n","b\n").Changes.Single().Kind=="삭제",
      "deleted first code line should be a deletion");
Check(CodeVersionComparer.Compare("", "added\n").Changes.Single().AfterStart==0,
      "empty-to-nonempty code file should be an insertion");
Check(CodeVersionComparer.Compare("obsolete\n", "").Changes.Single().AfterEnd==0,
      "nonempty-to-empty code file should preserve deletion anchor");
Console.WriteLine("PASS CODE FILE VERSION SNAPSHOTS AND LINE DIFF");

// 151. C# and Rust use identical half-open line spans, including terminal newlines.
var finalNewline=CodeVersionComparer.Compare("alpha\n","alpha");
Check(finalNewline.Changes.Count==1&&finalNewline.Changes[0].Kind=="교체"&&
      finalNewline.Changes[0].BeforeStart==0&&finalNewline.Changes[0].AfterEnd==1,
      "removing the final LF must not be reported as identical");
var crlf=CodeVersionComparer.Compare("한글\r\n","한글\n");
Check(crlf.Changes.Count==1&&crlf.Changes[0].Kind=="교체",
      "changing CRLF to LF must be recognized, matching Rust");
Check(CodeVersionComparer.Compare("a\nb\n","a\n").Changes.Single() is
      {Kind:"삭제",BeforeStart:1,BeforeEnd:2,AfterStart:1,AfterEnd:1},
      "last-line deletion must anchor at end of the after file");
Check(CodeVersionComparer.Compare("a\n","a\n\n").Changes.Single() is
      {Kind:"추가",BeforeStart:1,BeforeEnd:1,AfterStart:1,AfterEnd:2},
      "trailing blank line insertion must match Rust");
Check(CodeVersionComparer.DisplayLines("a\r\nb\n").SequenceEqual(new[]{"a","b"}),
      "preview must strip display-only CRLF while preserving comparison semantics");
var longBefore=string.Concat(Enumerable.Repeat("same\n",12000));
var longAfter=string.Concat(Enumerable.Repeat("same\n",11999))+"changed\n";
var longCode=CodeVersionComparer.Compare(longBefore,longAfter);
Check(longCode.Changes.Single().AfterStart==11999,
      "long code file must preserve the physical line anchor");
var visible=CodeVersionComparer.PreviewLineIndices(longCode.AfterLines.Length,longCode.Changes);
Check(visible.Contains(11999)&&visible.Contains(9999)&&visible.Count<=10003,
      "markers after 10,000 lines must have scrollable preview targets");
Check(CodeVersionComparer.PreviewLineIndices(0,CodeVersionComparer.Compare("x\n","").Changes).Count==0,
      "empty-after preview needs a placeholder anchor without a real line");
Console.WriteLine("PASS CODE CRLF/EOF PARITY AND LONG-FILE ANCHORS");

// 157. Keep both boundaries of a very long code change without emitting all
// intermediate rows. Navigation remains tied to original physical line numbers.
var longReplacement=CodeVersionComparer.Compare(
    string.Concat(Enumerable.Repeat("before\n",25000)),
    string.Concat(Enumerable.Repeat("after\n",25000)));
var longVisible=CodeVersionComparer.PreviewLineIndices(
    longReplacement.AfterLines.Length,longReplacement.Changes);
Check(longReplacement.Changes.Count==1 &&
      longVisible.Contains(0) && longVisible.Contains(24999) &&
      longVisible.Count<=10005,
      "large change end must remain navigable without rendering 25,000 preview rows");
Console.WriteLine("PASS LONG CODE CHANGE END BOUNDARY");




// 152. Explicit UTF-8 byte <-> UTF-16 code-unit offsets for future Rust interop.
var multibyteSource="A한😀\r\nZ";
Check(SourceTextOffsets.Utf8ByteToUtf16(multibyteSource,0)==0 &&
      SourceTextOffsets.Utf8ByteToUtf16(multibyteSource,4)==2 &&
      SourceTextOffsets.Utf8ByteToUtf16(multibyteSource,8)==4 &&
      SourceTextOffsets.Utf8ByteToUtf16(multibyteSource,11)==7,
      "Rust UTF-8 offsets must map to correct Korean/emoji/CRLF UTF-16 positions");
Check(SourceTextOffsets.Utf16ToUtf8Byte(multibyteSource,2)==4 &&
      SourceTextOffsets.Utf16ToUtf8Byte(multibyteSource,4)==8 &&
      SourceTextOffsets.Utf16ToUtf8Byte(multibyteSource,7)==11,
      "UTF-16 positions must round-trip to Rust UTF-8 byte indices");
try { SourceTextOffsets.Utf8ByteToUtf16(multibyteSource,5); throw new Exception("split UTF-8 code point accepted"); }
catch (ArgumentOutOfRangeException) { }
try { SourceTextOffsets.Utf16ToUtf8Byte(multibyteSource,3); throw new Exception("split emoji surrogate accepted"); }
catch (ArgumentOutOfRangeException) { }
Console.WriteLine("PASS RUST/C# UTF8-UTF16 OFFSET CONTRACT");

// 153. BOM must remain visible to the comparison engine, like Rust read_to_string.
var bomCodePath=Path.Combine(dir,"code-with-bom.rs");
File.WriteAllBytes(bomCodePath,new byte[]{0xEF,0xBB,0xBF,(byte)'x',(byte)'\n'});
var bomCode=CodeVersionComparer.ReadCodeFile(bomCodePath);
Check(bomCode=="\uFEFFx\n","UTF-8 BOM was stripped before code comparison");
Check(CodeVersionComparer.Compare("x\n",bomCode).Changes.Single() is
      {Kind:"교체",BeforeStart:0,BeforeEnd:1,AfterStart:0,AfterEnd:1},
      "BOM-only modification must be a change, matching Rust");
Console.WriteLine("PASS CODE BOM-PRESERVING RUST PARITY");

// 154. Pure large insert/delete should yield one span without an LCS allocation.
var manyLines=string.Concat(Enumerable.Repeat("x\n",100000));
Check(CodeVersionComparer.Compare("",manyLines).Changes.Single() is
      {Kind:"추가",BeforeStart:0,BeforeEnd:0,AfterStart:0,AfterEnd:100000},
      "large insertion should have one exact Rust-compatible span");
Check(CodeVersionComparer.Compare(manyLines,"").Changes.Single() is
      {Kind:"삭제",BeforeStart:0,BeforeEnd:100000,AfterStart:0,AfterEnd:0},
      "large deletion should have one exact Rust-compatible span");
Console.WriteLine("PASS CODE LARGE PURE INSERT/DELETE");

// 155. Linux path case is meaningful for source-version identity.
if(OperatingSystem.IsLinux())
{
    var caseDir=Path.Combine(dir,"source-case");
    Directory.CreateDirectory(caseDir);
    var upper=Path.Combine(caseDir,"Hello.rs");
    var lower=Path.Combine(caseDir,"hello.rs");
    File.WriteAllText(upper,"fn first() {}");
    File.WriteAllText(lower,"fn second() {}");
    var caseProject=new VersionHistoryProjectVm();
    var caseStore=VersionSnapshotStore.CreateTemporary();
    var resultCase=VersionFileImportPolicy.AddPaths(caseProject,new[]{upper,lower},caseStore);
    Check(resultCase.Added==2&&caseProject.Versions.Count==2,
          "case-distinct Linux paths must not be merged as one version");
    caseStore.CleanupTemporary();
}
Console.WriteLine("PASS OS-AWARE VERSION PATH IDENTITY");



// 158. Repeated and concurrent content-addressed snapshot imports are idempotent.
var concurrencyPath=Path.Combine(dir,"snapshot-parallel.cs");
File.WriteAllText(concurrencyPath,"class 병렬 { string S = \"😀\"; }\r\n",new UTF8Encoding(false));
var parallelStore=VersionSnapshotStore.CreateTemporary();
var parallelHash=VersionSnapshotStore.HashFile(concurrencyPath);
var importedHashes=await Task.WhenAll(Enumerable.Range(0,32).Select(_ =>
    Task.Run(()=>parallelStore.Import(concurrencyPath))));
Check(importedHashes.All(hash=>hash==parallelHash),
      "simultaneous imports of identical bytes must all resolve to the same blob");
var parallelVersion=new VersionFileVm
{
    Path=concurrencyPath,Sha256=parallelHash,SnapshotBlobId=parallelHash
};
Check(VersionSnapshotStore.HashFile(parallelStore.Resolve(parallelVersion))==parallelHash,
      "parallel imports must not corrupt a captured blob");
Check(!Directory.EnumerateFiles(parallelStore.Root,".staging-*",SearchOption.AllDirectories).Any(),
      "parallel imports left temporary data in the content store");
Console.WriteLine("PASS CONCURRENT SNAPSHOT IMPORT");

// Windows content store move/open share recovery must tolerate a brief
// exclusive writer without masking persistent IO errors or hash corruption.
if (OperatingSystem.IsWindows())
{
    var sharingFile=Path.Combine(dir,"snapshot-sharing-race.cs");
    File.WriteAllText(sharingFile,"class SnapshotSharing {}");
    using var writer=new FileStream(sharingFile,FileMode.Open,
        FileAccess.ReadWrite,FileShare.None);
    using var startedHash=new ManualResetEventSlim();
    var deferredHash=Task.Run(()=>{
        startedHash.Set();
        return VersionSnapshotStore.HashFile(sharingFile);
    });
    Check(startedHash.Wait(TimeSpan.FromSeconds(5)),
        "Windows snapshot lock contention task did not begin");
    await Task.Delay(40);
    writer.Dispose();
    Check(await deferredHash==VersionSnapshotStore.HashFile(sharingFile),
        "Windows snapshot hash should retry transient sharing violation");
    Console.WriteLine("PASS WINDOWS SNAPSHOT SHARING RACE RECOVERY");
}

// 159. Concurrent saves of the same project commit a complete manifest and sidecar.
var parallelProjectPath=Path.Combine(dir,"parallel-version.dcv.json");
var parallelProject=new VersionHistoryProjectVm
{
    Versions=new(){parallelVersion}
};
var parallelSaved=await Task.WhenAll(Enumerable.Range(0,12).Select(_ =>
    VersionHistoryProjectStore.SaveAsync(parallelProject,parallelStore,parallelProjectPath)));
var parallelFinal=System.Text.Json.JsonSerializer.Deserialize<VersionHistoryProjectVm>(
    File.ReadAllText(parallelProjectPath))!;
Check(parallelSaved.All(result=>result.Versions.Single().SnapshotBlobId==parallelHash) &&
      VersionSnapshotStore.HashFile(
          VersionSnapshotStore.ForProject(parallelProjectPath).Resolve(parallelFinal.Versions.Single()))==parallelHash,
      "concurrent saves must leave a complete parseable manifest and verified content");
Check(!Directory.EnumerateFiles(dir,Path.GetFileName(parallelProjectPath)+".tmp-*").Any(),
      "concurrent saves left temporary project manifests");
using(var canceledSave=new CancellationTokenSource())
{
    canceledSave.Cancel();
    var canceled=false;
    try
    {
        await VersionHistoryProjectStore.SaveAsync(parallelProject,parallelStore,
            Path.Combine(dir,"cancel-save.dcv.json"),canceledSave.Token);
    }
    catch(OperationCanceledException) { canceled=true; }
    Check(canceled,"cancelled project save must not proceed");
}
parallelStore.CleanupTemporary();
Console.WriteLine("PASS CONCURRENT AND CANCELABLE PROJECT SAVE");

// 160. The UI code comparison maps real byte/UTF-16 ranges to stable unified events.
var utfBefore="A한😀\r\nkeep\n";
var utfAfter="A한😀\nkeep\n";
var codeCompared=CodeVersionComparer.Compare(utfBefore,utfAfter);
var mappedEvents=CodeVersionChangeMapper.Map(utfBefore,utfAfter,codeCompared,
    "revision-old","revision-new","src/example.rs","src/example.rs");
var mappedEvent=mappedEvents.Single();
Check(mappedEvent.Kind==UnifiedChangeKind.Replace &&
      mappedEvent.Before is { StartByte:0, EndByte:10,StartUtf16:0,EndUtf16:6 } &&
      mappedEvent.After is { StartByte:0, EndByte:9,StartUtf16:0,EndUtf16:5 } &&
      mappedEvent.Before.VersionId=="revision-old" &&
      mappedEvent.After.RelativePath=="src/example.rs" &&
      mappedEvent.Before.Kind==UnifiedLocationKind.File,
      "CRLF/Korean/emoji source locations must preserve exact UTF-8 and UTF-16 spans");
var sameEvents=CodeVersionChangeMapper.Map(utfBefore,utfAfter,codeCompared,
    "revision-old","revision-new","src/example.rs","src/example.rs");
Check(sameEvents.Single().EventId==mappedEvent.EventId,
      "a display renumbering must not change the stable event identifier");
var insertedEvents=CodeVersionChangeMapper.Map("가\n","가\n😀\n",
    CodeVersionComparer.Compare("가\n","가\n😀\n"),
    "old","new","example.rs","example.rs");
Check(insertedEvents.Single() is
      { Kind: UnifiedChangeKind.Insert,
        Before: { StartByte:4,EndByte:4,StartUtf16:2,EndUtf16:2 },
        After: { StartByte:4,EndByte:9,StartUtf16:2,EndUtf16:5 } },
      "inserted line must map a zero-width old location and precise UTF offsets");
var removedEvents=CodeVersionChangeMapper.Map("가\n😀\n","가\n",
    CodeVersionComparer.Compare("가\n😀\n","가\n"),
    "old","new","example.rs","example.rs");
Check(removedEvents.Single() is
      { Kind: UnifiedChangeKind.Delete,
        Before: { StartByte:4,EndByte:9,StartUtf16:2,EndUtf16:5 },
        After: { StartByte:4,EndByte:4,StartUtf16:2,EndUtf16:2 } },
      "deleted line must map precise old location and zero-width new position");
var invalidSpanRejected=false;
try
{
    CodeVersionChangeMapper.Map("a\n","b\n",
        new CodeVersionComparer.Result(new[]{"a"},new[]{"b"},
            new[]{new CodeVersionComparer.LineChange("교체",0,5,0,1)}),
        "old","new","a.rs","b.rs");
}
catch(InvalidDataException) { invalidSpanRejected=true; }
Check(invalidSpanRejected,"out-of-bounds source positions must never produce unified events");
Console.WriteLine("PASS LIVE CODE UNIFIED EVENT UTF8-UTF16 MAPPING");


// 161. Exercise the loaded Rust C ABI, not just C# algorithm parity.
var nativeMustBePresent=Environment.GetEnvironmentVariable("DOCUMENTCOMPARE_REQUIRE_NATIVE")=="1";
Check(!nativeMustBePresent || NativeCodeVersionComparer.IsAvailable,
      "Windows/CI native package must embed a loadable Rust core");
var nativeFixtures=new (string Before,string After)[]
{
    ("",""), ("","a\n"), ("a\n",""), ("a\nb\n","a\nx\n"),
    ("가😀\r\n","가😀\n"), ("hello\n","hello"),
    ("repeat\nrepeat\n","repeat\n"), ("a\n","a\n\n"),
    ("\uFEFFheader\n","header\n"),
    (string.Concat(Enumerable.Repeat("before\n",2200)),
     string.Concat(Enumerable.Repeat("after\n",2200)))
};
foreach(var fixture in nativeFixtures)
{
    var nativeCompared=NativeCodeVersionComparer.Compare(fixture.Before,fixture.After,out var usedNative);
    Check(!nativeMustBePresent || usedNative, "Rust core was not used for code diff");
    var managedCompared=CodeVersionComparer.Compare(fixture.Before,fixture.After);
    Check(nativeCompared.Changes.SequenceEqual(managedCompared.Changes) &&
          nativeCompared.BeforeLines.SequenceEqual(managedCompared.BeforeLines) &&
          nativeCompared.AfterLines.SequenceEqual(managedCompared.AfterLines),
          "native Rust hunk or line display differs from managed fallback");
    var nativeEvents=CodeVersionChangeMapper.Map(fixture.Before,fixture.After,nativeCompared,
        "sha-before","sha-after","test.rs","test.rs");
    var managedEvents=CodeVersionChangeMapper.Map(fixture.Before,fixture.After,managedCompared,
        "sha-before","sha-after","test.rs","test.rs");
    Check(nativeEvents.SequenceEqual(managedEvents),
          "native comparison must yield the same unified change events and offsets");
}
Console.WriteLine(NativeCodeVersionComparer.IsAvailable
    ? "PASS RUST NATIVE FFI REAL LOADING AND EVENT PARITY"
    : "PASS NATIVE-ABSENT C# FALLBACK AND EVENT PARITY");

// 162. Repeated Rust FFI calls must safely free Rust allocations.
if(NativeCodeVersionComparer.IsAvailable)
{
    for(var iteration=0;iteration<500;iteration++)
    {
        var comparison=NativeCodeVersionComparer.Compare(
            "가😀\r\nkeep\n", "가😀\nkeep\nextra\n",out var usedNative);
        Check(usedNative&&comparison.Changes.Count==2,
              "repeated Rust FFI calls failed to preserve the change result");
    }
}
Console.WriteLine("PASS REPEATED RUST FFI RESOURCE LIFETIME");


// 163. Folder comparison is now an actual Rust FFI call, not just a Rust CLI.
var nativeFolderTest=Path.Combine(dir,"rust-folder-ffi-한글");
var nativeFolderBefore=Path.Combine(nativeFolderTest,"before");
var nativeFolderAfter=Path.Combine(nativeFolderTest,"after");
Directory.CreateDirectory(nativeFolderBefore);
Directory.CreateDirectory(nativeFolderAfter);
Directory.CreateDirectory(Path.Combine(nativeFolderBefore,"코드"));
Directory.CreateDirectory(Path.Combine(nativeFolderAfter,"이동"));
File.WriteAllText(Path.Combine(nativeFolderBefore,"코드","원본.rs"),"fn main(){println!(\"😀\");}\n");
File.WriteAllText(Path.Combine(nativeFolderAfter,"이동","변경.rs"),"fn main(){println!(\"😀\");}\n");
File.WriteAllText(Path.Combine(nativeFolderBefore,"edited.cs"),"int a=1;");
File.WriteAllText(Path.Combine(nativeFolderAfter,"edited.cs"),"int a=2;");
File.WriteAllText(Path.Combine(nativeFolderBefore,"removed.py"),"old_only");
File.WriteAllText(Path.Combine(nativeFolderAfter,"created.ts"),"new_only");
File.WriteAllText(Path.Combine(nativeFolderBefore,"identical.txt"),"stay");
File.WriteAllText(Path.Combine(nativeFolderAfter,"identical.txt"),"stay");
if(NativeCodeVersionComparer.IsAvailable)
{
    var folderNative=NativeCodeVersionComparer.CompareFolders(nativeFolderBefore,nativeFolderAfter);
    var expectedFolderChanges=new[]
    {
        new NativeCodeVersionComparer.FolderChange("수정","edited.cs","edited.cs"),
        new NativeCodeVersionComparer.FolderChange("삭제","removed.py",""),
        new NativeCodeVersionComparer.FolderChange("추가","","created.ts"),
        new NativeCodeVersionComparer.FolderChange("이동/이름 변경","코드/원본.rs","이동/변경.rs")
    };
    Check(folderNative.Count==expectedFolderChanges.Length &&
          folderNative.All(entry=>expectedFolderChanges.Contains(entry)),
          "Rust FFI folder added/deleted/modified/moved result mismatch");
    var repeatedFolder=NativeCodeVersionComparer.CompareFolders(nativeFolderBefore,nativeFolderAfter);
    Check(repeatedFolder.SequenceEqual(folderNative),
          "Rust FFI folder events changed between identical runs");
    var sameFolder=NativeCodeVersionComparer.CompareFolders(nativeFolderBefore,nativeFolderBefore);
    Check(sameFolder.Count==0,"same-source folder comparison must be empty");
    var missingFolderRejected=false;
    try { NativeCodeVersionComparer.CompareFolders(nativeFolderBefore,Path.Combine(dir,"folder-missing")); }
    catch(DirectoryNotFoundException) { missingFolderRejected=true; }
    Check(missingFolderRejected,"missing folder must be rejected before native call");
}
else
{
    var requiredNative= false;
    try { NativeCodeVersionComparer.CompareFolders(nativeFolderBefore,nativeFolderAfter); }
    catch(NotSupportedException) { requiredNative=true; }
    Check(requiredNative, "native-absent folder comparison must fail explicitly, not silently");
}
Console.WriteLine("PASS RUST FOLDER FFI AND EXPLICIT FALLBACK");


// 164. Actual Tree-sitter Rust/C# AST detection through the shared native ABI.
if(NativeCodeVersionComparer.IsAvailable)
{
    var rustBefore="mod first { fn hello() { println!(\"안녕😀\"); } }\nmod second {}\n";
    var rustAfter="mod first {}\nmod second { fn hello() { println!(\"안녕😀\"); } }\n";
    var astMoved=NativeCodeVersionComparer.CompareAst(".rs",rustBefore,rustAfter);
    Check(astMoved.Supported && astMoved.Changes.Count==1 &&
          astMoved.Changes.Single() is {Kind:"Move",BeforeName:"hello",AfterName:"hello"} &&
          astMoved.Changes[0].AfterStart > astMoved.Changes[0].BeforeStart,
          "Rust Tree-sitter must detect moved function by unique identical body");
    var astRenamed=NativeCodeVersionComparer.CompareAst(".cs",
        "class C { void First() { int x=1; } }",
        "class C { void Second() { int x=1; } }");
    Check(astRenamed.Supported && astRenamed.Changes.Any(
            c=>c.Kind=="Rename" && c.BeforeName=="First" && c.AfterName=="Second"),
          "C# Tree-sitter must recognize function rename with unchanged body");
    var astInvalid=NativeCodeVersionComparer.CompareAst(".rs","fn x() {", "fn x() {}");
    Check(!astInvalid.Supported && astInvalid.Changes.Count==0 &&
          !string.IsNullOrWhiteSpace(astInvalid.Reason),
          "syntax errors must be reported as unsupported AST, never as no changes");
    var astOther=NativeCodeVersionComparer.CompareAst(".txt","x","y");
    Check(!astOther.Supported,"unknown language must stay on line-diff mode");
    var astPlain=NativeCodeVersionComparer.CompareAst(".rs","fn same() { 1; }","fn same() { 1; }");
    Check(astPlain.Supported && astPlain.Changes.Count==0,
          "valid unchanged declarations must return explicit empty AST results");
}
Console.WriteLine("PASS RUST CSHARP TREE SITTER AST FFI");


// 165. AST syntax offsets navigate exact lines with CRLF, UTF-8 and emoji.
var astIndex=CodeAstLineLocation.Index("가😀\r\nline\n마지막");
Check(astIndex.LineForByte(0)==0 &&
      astIndex.LineForByte(3)==0 &&
      astIndex.LineForByte(8)==0 &&
      astIndex.LineForByte(9)==1 &&
      astIndex.LineForByte(14)==2,
      "AST UTF-8 byte locations must resolve to correct displayed physical lines");
try { astIndex.LineForByte(-1); throw new Exception("negative byte index accepted"); }
catch(ArgumentOutOfRangeException) { }
Console.WriteLine("PASS AST UTF8 PHYSICAL LINE ANCHORS");


// 166. Real Rust FFI cross-file move detection and additive presentation contract.
var crossRoot=Path.Combine(dir,"cross-file-ffi-한글");
var crossBefore=Path.Combine(crossRoot,"previous");
var crossAfter=Path.Combine(crossRoot,"current");
Directory.CreateDirectory(crossBefore);
Directory.CreateDirectory(crossAfter);
File.WriteAllText(Path.Combine(crossBefore,"left.rs"),
    "fn moved() { let greeting = \"안녕😀\"; }\nfn unchanged() { let k = 1; }\n");
File.WriteAllText(Path.Combine(crossAfter,"left.rs"),
    "fn unchanged() { let k = 1; }\n");
File.WriteAllText(Path.Combine(crossAfter,"right.rs"),
    "fn moved() { let greeting = \"안녕😀\"; }\n");
File.WriteAllText(Path.Combine(crossAfter,"asset.png"),"fake binary");
if(NativeCodeVersionComparer.IsAvailable)
{
    var fileChanges=NativeCodeVersionComparer.CompareFolders(crossBefore,crossAfter);
    var cross=NativeCodeVersionComparer.CompareCrossFileAst(crossBefore,crossAfter);
    Check(cross.Supported&&cross.Moves.Count==1&&cross.SkippedFiles==0 &&
          cross.Moves[0] is
          {SymbolKind:"function_item", SymbolName:"moved",BeforePath:"left.rs",AfterPath:"right.rs"} &&
          cross.AnalyzedFiles==3,
          "Rust cross-file FFI must find uniquely identical Korean/emoji declaration moved between files");
    var merged=FolderAstReviewMapper.Merge(fileChanges,cross);
    Check(merged.Count==fileChanges.Count+1 &&
          merged.Take(fileChanges.Count).SequenceEqual(fileChanges) &&
          merged[^1].Kind=="선언 이동(AST)" &&
          !string.IsNullOrWhiteSpace(merged[^1].EventId) &&
          merged[^1].BeforePath.Contains("left.rs") &&
          merged[^1].AfterPath.Contains("right.rs"),
          "file-level entries must be preserved with separate stable AST move event");
    var again=FolderAstReviewMapper.Merge(fileChanges,
        NativeCodeVersionComparer.CompareCrossFileAst(crossBefore,crossAfter));
    Check(again.SequenceEqual(merged),"cross-file event ID and order must remain stable");
    var unsupported=new NativeCodeVersionComparer.CrossFileAstReport(
        Array.Empty<NativeCodeVersionComparer.CrossFileMove>(),0,0,false,"budget exceeded");
    Check(FolderAstReviewMapper.Merge(fileChanges,unsupported).SequenceEqual(fileChanges),
          "unavailable AST must never erase native file-level results");
    File.WriteAllText(Path.Combine(crossBefore,"malformed.rs"),"fn broken() {");
    File.WriteAllText(Path.Combine(crossAfter,"malformed.rs"),"fn broken() { let n=2; }");
    var partial=NativeCodeVersionComparer.CompareCrossFileAst(crossBefore,crossAfter);
    Check(partial.Supported&&partial.SkippedFiles>=1&&partial.Moves.Count==1,
          "invalid Rust file must be explicitly reported as skipped without hiding valid matches");
}
Console.WriteLine("PASS CROSS FILE RUST AST MOVE FFI AND PRESENTATION");


// 167. Real modified-and-moved Rust/C# methods from the native Tree-sitter FFI.
var editedFolderRoot=Path.Combine(dir,"modified-cross-file-ffi");
var editedOld=Path.Combine(editedFolderRoot,"before");
var editedNew=Path.Combine(editedFolderRoot,"after");
Directory.CreateDirectory(editedOld);
Directory.CreateDirectory(editedNew);
File.WriteAllText(Path.Combine(editedOld,"left.rs"),
    "fn compute(input: i32) -> i32 {\n let offset = 10;\n let doubled = input * 2;\n let result = doubled + offset;\n println!(\"result {}\", result);\n result\n}\nfn stay() { let unused=1; }");
File.WriteAllText(Path.Combine(editedNew,"left.rs"),"fn stay() { let unused=1; }");
File.WriteAllText(Path.Combine(editedNew,"dest.rs"),
    "fn compute(input: i32) -> i32 {\n let offset = 11;\n let doubled = input * 2;\n let result = doubled + offset;\n println!(\"result {}\", result);\n result\n}\n");
File.WriteAllText(Path.Combine(editedOld,"One.cs"),
    "class One { int Work(int value) { var offset = 10; var doubled = value * 2; var answer = doubled + offset; System.Console.WriteLine(answer); return answer; } }");
File.WriteAllText(Path.Combine(editedNew,"Two.cs"),
    "class Two { int Work(int value) { var offset = 12; var doubled = value * 2; var answer = doubled + offset; System.Console.WriteLine(answer); return answer; } }");
if(NativeCodeVersionComparer.IsAvailable)
{
    var report=NativeCodeVersionComparer.CompareCrossFileAst(editedOld,editedNew);
    Check(report.Supported && report.Moves.Count==2 &&
          report.Moves.All(m=>m.ContentChanged &&
              m.SimilarityPermille is >= 880 and < 1000) &&
          report.Moves.Any(m=>m is {SymbolName:"compute",BeforePath:"left.rs",AfterPath:"dest.rs"}) &&
          report.Moves.Any(m=>m is {SymbolName:"Work",BeforePath:"One.cs",AfterPath:"Two.cs"}),
          "Native FFI must detect moved-and-edited Rust and C# methods with guarded high similarity");
    var fileChanges=NativeCodeVersionComparer.CompareFolders(editedOld,editedNew);
    var rendered=FolderAstReviewMapper.Merge(fileChanges,report);
    Check(rendered.Count==fileChanges.Count+2 &&
          rendered.Skip(fileChanges.Count).All(c=>
              c.Kind=="이동·수정 후보(AST)" &&
              c.AfterPath.Contains("어휘 유사도") &&
              !string.IsNullOrWhiteSpace(c.EventId)),
          "Heuristic moved+edited candidates must be labeled as candidates, not exact moves");
    var repeat=FolderAstReviewMapper.Merge(fileChanges,
        NativeCodeVersionComparer.CompareCrossFileAst(editedOld,editedNew));
    Check(repeat.SequenceEqual(rendered),
          "Moved+edited candidate identity/order must remain deterministic");
}
Console.WriteLine("PASS MODIFIED CROSS FILE AST NATIVE FFI GUARDED CANDIDATES");


// 168. Edited formatting can retain 100% lexical similarity without byte identity.
var formattedRoot=Path.Combine(dir,"formatted-cross-file");
var formattedBefore=Path.Combine(formattedRoot,"old");
var formattedAfter=Path.Combine(formattedRoot,"new");
Directory.CreateDirectory(formattedBefore);
Directory.CreateDirectory(formattedAfter);
File.WriteAllText(Path.Combine(formattedBefore,"old.rs"),
    "fn process(value: i32) -> i32 { let doubled = value * 2; let offset = 10; let answer = doubled + offset; println!(\"answer {}\", answer); answer }");
File.WriteAllText(Path.Combine(formattedAfter,"new.rs"),
    "fn process(value: i32) -> i32 {\n let doubled = value * 2;\n let offset = 10;\n let answer = doubled + offset;\n println!(\"answer {}\", answer);\n answer\n}");
if(NativeCodeVersionComparer.IsAvailable)
{
    var formattedMove=NativeCodeVersionComparer.CompareCrossFileAst(formattedBefore,formattedAfter);
    Check(formattedMove.Supported && formattedMove.Moves.Count==1 &&
          formattedMove.Moves[0].ContentChanged &&
          formattedMove.Moves[0].SimilarityPermille==1000,
          "whitespace-edited function may have exact lexical similarity but remains a candidate");
    var combined=FolderAstReviewMapper.Merge(
        NativeCodeVersionComparer.CompareFolders(formattedBefore,formattedAfter),formattedMove);
    Check(combined[^1].Kind=="이동·수정 후보(AST)",
          "format-only move must not be mislabeled as an exact-byte move");
}
Console.WriteLine("PASS FORMATTING ONLY MOVED METHOD CANDIDATE");


// 169. Real immutable Git commit/tree lineage; no checkout or working-tree writes.
var gitFixture=Path.Combine(dir,"git-commit-lineage");
Directory.CreateDirectory(gitFixture);
static string GitExec(string root,params string[] arguments)
{
    using var process=new System.Diagnostics.Process();
    process.StartInfo=new System.Diagnostics.ProcessStartInfo("git")
    {
        WorkingDirectory=root,
        UseShellExecute=false, RedirectStandardOutput=true,
        RedirectStandardError=true, CreateNoWindow=true
    };
    foreach(var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
    process.Start();
    var output=process.StandardOutput.ReadToEnd();
    var error=process.StandardError.ReadToEnd();
    process.WaitForExit();
    if(process.ExitCode!=0)throw new Exception("Git test command failed: "+string.Join(" ",arguments)+" : "+error);
    return output.Trim();
}
GitExec(gitFixture,"init","-q");
GitExec(gitFixture,"config","user.name","Regression Tester");
GitExec(gitFixture,"config","user.email","test@example.invalid");
Directory.CreateDirectory(Path.Combine(gitFixture,"한글"));
File.WriteAllText(Path.Combine(gitFixture,"한글","원본.rs"),"fn original(){println!(\"가😀\");}\n");
File.WriteAllText(Path.Combine(gitFixture,"modify.txt"),"before\n");
File.WriteAllText(Path.Combine(gitFixture,"deleted.txt"),"gone\n");
File.WriteAllText(Path.Combine(gitFixture,"ambiguous-a.txt"),"identical\n");
File.WriteAllText(Path.Combine(gitFixture,"ambiguous-b.txt"),"identical\n");
var historyLines=Enumerable.Range(0,35).Select(i=>$"line number {i:D2} has stable text\n").ToArray();
File.WriteAllText(Path.Combine(gitFixture,"old-name.cs"),string.Concat(historyLines));
GitExec(gitFixture,"add",".");
GitExec(gitFixture,"commit","-q","-m","initial");
var initialCommit=GitExec(gitFixture,"rev-parse","HEAD");
Directory.CreateDirectory(Path.Combine(gitFixture,"new location"));
File.Move(Path.Combine(gitFixture,"한글","원본.rs"),
          Path.Combine(gitFixture,"new location","moved.rs"));
File.Move(Path.Combine(gitFixture,"old-name.cs"),Path.Combine(gitFixture,"new-name.cs"));
var editedHistory=(string[])historyLines.Clone();
editedHistory[17]="line number 17 changed by rename update\n";
File.WriteAllText(Path.Combine(gitFixture,"new-name.cs"),string.Concat(editedHistory));
File.Delete(Path.Combine(gitFixture,"deleted.txt"));
File.WriteAllText(Path.Combine(gitFixture,"modify.txt"),"after\n");
File.WriteAllText(Path.Combine(gitFixture,"added.txt"),"new\n");
GitExec(gitFixture,"add","-A");
GitExec(gitFixture,"commit","-q","-m","rename and update");
var finalCommit=GitExec(gitFixture,"rev-parse","HEAD");
var gitLineage=GitCommitLineageComparer.Compare(gitFixture,initialCommit,finalCommit);
Check(gitLineage.BeforeCommit==initialCommit &&
      gitLineage.AfterCommit==finalCommit &&
      gitLineage.Changes.Count==5 &&
      gitLineage.Changes.Any(c=>c is {
          Kind:"정확한 파일 이동(Git)",BeforePath:"한글/원본.rs",AfterPath:"new location/moved.rs"
      } && c.BeforeBlob==c.AfterBlob) &&
      gitLineage.Changes.Any(c=>c is {
          Kind:"이름 변경 후보(Git)",BeforePath:"old-name.cs",AfterPath:"new-name.cs"
      } && c.BeforeBlob!=c.AfterBlob && c.SimilarityPercent>=75) &&
      gitLineage.Changes.Any(c=>c is {Kind:"수정(Git)",BeforePath:"modify.txt"}) &&
      gitLineage.Changes.Any(c=>c is {Kind:"삭제(Git)",BeforePath:"deleted.txt"}) &&
      gitLineage.Changes.Any(c=>c is {Kind:"추가(Git)",AfterPath:"added.txt"}),
      "Git immutable tree comparison must distinguish modified/renamed/added/deleted and Unicode exact blob moves");
var untracked=Path.Combine(gitFixture,"uncommitted-local.txt");
File.WriteAllText(untracked,"local changes never influence historical tree");
var sameLineage=GitCommitLineageComparer.Compare(gitFixture,"HEAD~1","HEAD");
Check(sameLineage.Changes.SequenceEqual(gitLineage.Changes) &&
      File.Exists(untracked) &&
      GitExec(gitFixture,"status","--porcelain").Contains("uncommitted-local.txt"),
      "Git lineage must use object database, ignore uncommitted files and not checkout or edit");
var invertedRejected=false;
try{GitCommitLineageComparer.Compare(gitFixture,"HEAD","HEAD~1");}
catch(InvalidDataException){invertedRejected=true;}
Check(invertedRejected,"Git history must reject reversed ancestry");
var injectedRejected=false;
try{GitCommitLineageComparer.Compare(gitFixture,"--help","HEAD");}
catch(ArgumentException){injectedRejected=true;}
Check(injectedRejected,"Git revision options must be rejected");
var reviewGit=GitLineageReviewMapper.Map(gitLineage);
Check(reviewGit.Count==gitLineage.Changes.Count &&
      reviewGit.All(entry=>!string.IsNullOrWhiteSpace(entry.EventId)) &&
      reviewGit.Any(entry=>entry.Kind=="이름 변경 후보(Git)" &&
          entry.AfterPath.Contains("Git 유사도")) &&
      reviewGit.SequenceEqual(GitLineageReviewMapper.Map(sameLineage)),
      "Git commit-file events must have deterministic IDs and explicit similarity candidate labels");
var sameCommitTree=GitCommitLineageComparer.Compare(gitFixture,"HEAD","HEAD");
Check(sameCommitTree.Changes.Count==0,"identical Git commits must compare equal");
var nestedGitResult=GitCommitLineageComparer.Compare(
    Path.Combine(gitFixture,"new location"),"HEAD~1","HEAD");
static string NormalizedGitPath(string path) =>
    Path.GetFullPath(path).Replace('\\','/').TrimEnd('/');
Check(nestedGitResult.Changes.SequenceEqual(gitLineage.Changes) &&
      string.Equals(NormalizedGitPath(nestedGitResult.RepositoryRoot),
          NormalizedGitPath(gitFixture),
          OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
      "selecting a nested path must compare the same repository root");
File.Delete(untracked);
File.Move(Path.Combine(gitFixture,"ambiguous-a.txt"),
          Path.Combine(gitFixture,"ambiguous-moved.txt"));
GitExec(gitFixture,"add","-A");
GitExec(gitFixture,"commit","-q","-m","ambiguous duplicate blob move");
var duplicatedTree=GitCommitLineageComparer.Compare(gitFixture,finalCommit,"HEAD");
Check(duplicatedTree.Changes.Count==2 &&
      duplicatedTree.Changes.All(c=>c.Kind is "삭제(Git)" or "추가(Git)") &&
      !duplicatedTree.Changes.Any(c=>c.Kind=="정확한 파일 이동(Git)"),
      "a duplicate blob also present in unchanged paths must not prove an exact rename");
Console.WriteLine("PASS GIT COMMIT TREE LINEAGE AND READONLY SAFETY");


// 170. AST lineage is derived from immutable Git blobs (not the current working tree).
var gitAstRoot=Path.Combine(dir,"git-ast-lineage-한글");
Directory.CreateDirectory(gitAstRoot);
GitExec(gitAstRoot,"init","-q");
GitExec(gitAstRoot,"config","user.name","Regression Tester");
GitExec(gitAstRoot,"config","user.email","test@example.invalid");
var methodOld=
    "fn compute(input: i32) -> i32 {\n let offset = 10;\n let doubled = input * 2;\n let result = doubled + offset;\n println!(\"결과 {}\", result);\n result\n}\n";
var methodUpdated=
    "fn compute(input: i32) -> i32 {\n let offset = 11;\n let doubled = input * 2;\n let result = doubled + offset;\n println!(\"결과 {}\", result);\n result\n}\n";
File.WriteAllText(Path.Combine(gitAstRoot,"source.rs"),
    methodOld+"fn remain() { let unused = 3; }\n");
GitExec(gitAstRoot,"add",".");
GitExec(gitAstRoot,"commit","-q","-m","initial symbol");
var symbolBase=GitExec(gitAstRoot,"rev-parse","HEAD");
File.WriteAllText(Path.Combine(gitAstRoot,"source.rs"),"fn remain() { let unused = 3; }\n");
File.WriteAllText(Path.Combine(gitAstRoot,"target.rs"),methodOld);
GitExec(gitAstRoot,"add","-A");
GitExec(gitAstRoot,"commit","-q","-m","move method");
var symbolMiddle=GitExec(gitAstRoot,"rev-parse","HEAD");
File.WriteAllText(Path.Combine(gitAstRoot,"target.rs"),methodUpdated);
GitExec(gitAstRoot,"add","-A");
GitExec(gitAstRoot,"commit","-q","-m","edit method");
var symbolFinal=GitExec(gitAstRoot,"rev-parse","HEAD");
if(NativeCodeVersionComparer.IsAvailable)
{
    var firstHistory=GitCommitLineageComparer.Compare(gitAstRoot,symbolBase,symbolMiddle);
    var firstAst=GitAstLineageComparer.Compare(firstHistory);
    Check(firstAst.Supported && firstAst.SkippedFiles==0 &&
          firstAst.Moves.Count==1 && !firstAst.Moves[0].ContentChanged &&
          firstAst.Moves[0] is {BeforePath:"source.rs",AfterPath:"target.rs",SymbolName:"compute"},
          "immutable Git blobs must detect an exact symbol move between commits");
    var allHistory=GitCommitLineageComparer.Compare(gitAstRoot,symbolBase,symbolFinal);
    var timeline=GitAstHistoryComparer.Compare(allHistory);
    Check(timeline.Supported && timeline.Steps.Count==2 &&
          timeline.Steps[0].BeforeCommit==symbolBase &&
          timeline.Steps[0].AfterCommit==symbolMiddle &&
          timeline.Steps[0].Ast.Supported &&
          timeline.Steps[0].Ast.Moves.Count==1 &&
          !timeline.Steps[0].Ast.Moves[0].ContentChanged &&
          timeline.Steps[1].BeforeCommit==symbolMiddle &&
          timeline.Steps[1].AfterCommit==symbolFinal &&
          timeline.Steps[1].Ast.Moves.Count==0,
          "Git AST timeline must identify movement in its actual intermediate commit");
    var limitedStepsRejected=false;
    try { GitCommitLineageComparer.FirstParentPath(allHistory,1); }
    catch(InvalidDataException) { limitedStepsRejected=true; }
    Check(limitedStepsRejected,
          "Git AST history must explicitly reject a first-parent interval longer than its step budget");
    var timelineReview=GitLineageReviewMapper.WithTimeline(allHistory,timeline);
    Check(timelineReview.Count==allHistory.Changes.Count+1 &&
          timelineReview[^1].Kind=="커밋별 선언 이동(AST)" &&
          timelineReview[^1].Section?.Contains(symbolBase[..12])==true &&
          timelineReview[^1].Section?.Contains(symbolMiddle[..12])==true &&
          !string.IsNullOrWhiteSpace(timelineReview[^1].EventId),
          "Git timeline review must identify the precise adjacent commit edge");
    var allAst=GitAstLineageComparer.Compare(allHistory);
    Check(allAst.Supported && allAst.SkippedFiles==0 &&
          allAst.Moves.Count==1 && allAst.Moves[0].ContentChanged &&
          allAst.Moves[0].SimilarityPermille>=880 &&
          allAst.Moves[0].BeforePath=="source.rs" && allAst.Moves[0].AfterPath=="target.rs",
          "multi-commit before/after snapshot must identify modified-and-moved function as candidate");
    var joined=GitLineageReviewMapper.WithAst(allHistory,allAst);
    Check(joined.Count==allHistory.Changes.Count+1 &&
          joined.Last().Kind=="Git 함수 이동·수정 후보(AST)" &&
          joined.Last().AfterPath.Contains("유사도") &&
          !string.IsNullOrEmpty(joined.Last().EventId),
          "Git and AST must display separate stable file vs symbol lineage events");
    var again=GitLineageReviewMapper.WithAst(allHistory,GitAstLineageComparer.Compare(allHistory));
    Check(joined.SequenceEqual(again),"Git AST event IDs must remain stable across repeat runs");
    File.WriteAllText(Path.Combine(gitAstRoot,"source.rs"),"fn fake() { let fake = 123; }");
    File.WriteAllText(Path.Combine(gitAstRoot,"target.rs"),"fn fake() { let fake = 456; }");
    var uncommitted=GitAstLineageComparer.Compare(allHistory);
    Check(uncommitted.Moves.SequenceEqual(allAst.Moves) &&
          uncommitted.SkippedFiles==allAst.SkippedFiles,
          "uncommitted source changes must never affect Git AST analysis");
    var unsupported=new NativeCodeVersionComparer.CrossFileAstReport(
        Array.Empty<NativeCodeVersionComparer.CrossFileMove>(),0,0,false,"test simulated limit");
    Check(GitLineageReviewMapper.WithAst(allHistory,unsupported)
          .SequenceEqual(GitLineageReviewMapper.Map(allHistory)),
          "AST failure must never erase the Git file history");
    var unrelated=GitAstLineageComparer.Compare(
        GitCommitLineageComparer.Compare(gitAstRoot,symbolFinal,symbolFinal));
    Check(unrelated.Supported && unrelated.Moves.Count==0,
          "unchanged commit pair has no AST move events");
    File.WriteAllText(Path.Combine(gitAstRoot,"corrupt.rs"),"fn broken() {");
    GitExec(gitAstRoot,"add","-A");
    GitExec(gitAstRoot,"commit","-q","-m","invalid Rust source");
    var corruptedHistory=GitCommitLineageComparer.Compare(gitAstRoot,symbolFinal,"HEAD");
    var corruptedAst=GitAstLineageComparer.Compare(corruptedHistory);
    var corruptedTimeline=GitAstHistoryComparer.Compare(corruptedHistory);
    Check(corruptedAst.Supported && corruptedAst.SkippedFiles>=1 &&
          corruptedTimeline.Supported && corruptedTimeline.Steps.Count==1 &&
          corruptedTimeline.Steps[0].Ast.SkippedFiles>=1 &&
          GitLineageReviewMapper.WithTimeline(corruptedHistory,corruptedTimeline)
            .Take(corruptedHistory.Changes.Count)
            .SequenceEqual(GitLineageReviewMapper.Map(corruptedHistory)),
          "malformed committed source must be counted as skipped while Git file evidence stays intact");
}
Console.WriteLine("PASS IMMUTABLE GIT COMMIT AST SYMBOL MOVE AND MODIFIED MOVE");


// 171. Rename+signature change across committed files is always a candidate,
// never an asserted exact identity; grouped chronological section is stable.
var refactorRoot=Path.Combine(dir,"git-refactor-lineage");
Directory.CreateDirectory(refactorRoot);
GitExec(refactorRoot,"init","-q");
GitExec(refactorRoot,"config","user.name","Regression Tester");
GitExec(refactorRoot,"config","user.email","test@example.invalid");
var bodyRefactor="{ let offset = 10; let doubled = input * 2; let total = doubled + offset; println!(\"answer: {}\", total); let result = total + 42; println!(\"result: {}\", result); result }";
File.WriteAllText(Path.Combine(refactorRoot,"original.rs"),
    "fn calculate(input: i32) -> i32 "+bodyRefactor);
GitExec(refactorRoot,"add",".");
GitExec(refactorRoot,"commit","-q","-m","before refactor");
var refactorBefore=GitExec(refactorRoot,"rev-parse","HEAD");
File.Delete(Path.Combine(refactorRoot,"original.rs"));
File.WriteAllText(Path.Combine(refactorRoot,"refactored.rs"),
    "fn sum_value(input: i64) -> i64 "+bodyRefactor.Replace("offset = 10","offset = 11"));
GitExec(refactorRoot,"add","-A");
GitExec(refactorRoot,"commit","-q","-m","rename signature and move");
var refactorHistory=GitCommitLineageComparer.Compare(refactorRoot,refactorBefore,"HEAD");
if(NativeCodeVersionComparer.IsAvailable)
{
    var proposal=GitAstLineageComparer.Compare(refactorHistory);
    Check(proposal.Supported && proposal.Moves.Count==1 &&
          proposal.Moves[0] is
          {Renamed:true,SignatureChanged:true,ContentChanged:true,
           BeforeName:"calculate",AfterName:"sum_value"} &&
          proposal.Moves[0].SimilarityPermille>=950,
          "Native Rust Tree-sitter must report renamed+signature-changed move only as high-confidence candidate");
    var lineageView=GitLineageReviewMapper.WithAst(refactorHistory,proposal);
    Check(lineageView[^1].Kind=="Git 이름·서명 변경 후보(AST)" &&
          lineageView[^1].BeforePath.Contains("calculate") &&
          lineageView[^1].AfterPath.Contains("sum_value") &&
          lineageView[^1].Section=="양끝 커밋의 구조 비교",
          "Git refactor candidate must include both names and grouped endpoint provenance");
    var series=GitAstHistoryComparer.Compare(refactorHistory);
    var timelineView=GitLineageReviewMapper.WithTimeline(refactorHistory,series);
    Check(series.Supported && series.Steps.Count==1 &&
          timelineView[^1].Kind=="커밋별 이름·서명 변경 후보(AST)" &&
          timelineView[^1].Section?.Contains(refactorBefore[..12])==true &&
          timelineView[^1].Section?.Contains(series.Steps[0].AfterCommit[..12])==true &&
          !string.IsNullOrWhiteSpace(timelineView[^1].EventId),
          "Git timeline must retain commit-by-commit identity for refactor candidates");
}
Console.WriteLine("PASS GIT AST RENAMED SIGNATURE MOVE AND COMMIT TIMELINE SECTIONS");


// 172. Word/document-engine whitespace slice: byte-exact C# reference vs
// Rust FFI, including the famous "동의없이" -> "동의 없이" regression.
// Full Word paragraph/table/part judgment remains managed and must not regress.
if(NativeCodeVersionComparer.IsAvailable)
{
    var whitespaceMethod=typeof(NativeComparisonEngine).GetMethod(
        "TryWhitespaceBoundaryDiff",BindingFlags.Static|BindingFlags.NonPublic)!;
    var matchProperty=typeof(NativeComparisonEngine).GetProperty(
        "WhitespaceRustParityMatches",BindingFlags.Static|BindingFlags.NonPublic)!;
    var mismatchProperty=typeof(NativeComparisonEngine).GetProperty(
        "WhitespaceRustParityMismatches",BindingFlags.Static|BindingFlags.NonPublic)!;
    long BeforeMatch()=>(long)matchProperty.GetValue(null)!;
    long BeforeMismatch()=>(long)mismatchProperty.GetValue(null)!;
    var startingMatches=BeforeMatch();
    var startingMismatches=BeforeMismatch();
    var corpus=new List<(string,string)>
    {
        ("동의없이", "동의 없이"),
        ("동의 없이", "동의없이"),
        ("이용자의 동의없이 제3자에게", "이용자의 동의 없이 제3자에게"),
        ("제 1 조 · 동의없이", "제1조 · 동의 없이"),
        ("😀동의없이", "😀동의 없이"),
        ("한 글", "한글"),
        ("코드_word", "코드_ word"),
        ("  시작 문장", " 시작 문장"),
        ("문장 마지막", "문장 마지막 "),
        ("안녕\n세계", "안녕 세계"),
        ("동의없이", "동의 없이"), // repeated call must be deterministic
        ("동의없이", "동의없음"), // not whitespace-only
        ("", ""), ("  ", "\t"), ("동의", "동의"),
        ("a\u00a0b", "a b"), ("a\u3000b", "a b"),
        ("가😀나", "가😀 나"),
        ("X⁰Y", "X⁰ Y"),
        ("A\u2028B", "A B"),
        ("A\u0085B", "A B"),
    };
    var randomWhitespace=new Random(20261022);
    string[] words = {"동의", "없이", "개인정보", "제3자", "이용자", "보호", "본문", "abc", "_code", "한글", "😀", "Ω", "1", ".", "§"};
    string[] gaps = {"", "", "", " ", "  ", "\t", "\r\n", "\u00a0", "\u3000", "\u202f"};
    for(var n=0;n<500;n++)
    {
        var count=randomWhitespace.Next(1,16);
        var parts=Enumerable.Range(0,count).Select(_=>words[randomWhitespace.Next(words.Length)]).ToArray();
        var left=parts[0]; var right=parts[0];
        for(var i=1;i<count;i++)
        {
            left+=gaps[randomWhitespace.Next(gaps.Length)]+parts[i];
            right+=gaps[randomWhitespace.Next(gaps.Length)]+parts[i];
        }
        corpus.Add((left,right));
    }
    var eligible=0;
    foreach(var (oldText,newText) in corpus)
    {
        var native=NativeCodeVersionComparer.CompareDocumentWhitespace(oldText,newText);
        Check(native is not null, "Rust whitespace FFI must return an explicit eligible/ineligible result");
        var raw=whitespaceMethod.Invoke(null,new object[]{oldText,newText,0,0,0,1,"A-B",0,"body",0});
        var original=new List<NativeCodeVersionComparer.DocumentWhitespaceSpan>();
        if(raw is System.Collections.IEnumerable markers)
        {
            foreach(var marker in markers)
            {
                var endpoints=(System.Collections.IEnumerable)marker!.GetType().GetProperty("Endpoints")!.GetValue(marker)!;
                var spans=endpoints.Cast<object>().ToArray();
                Check(spans.Length==2, "whitespace change marker must have two endpoint spans");
                var oldEp=spans[0]; var newEp=spans[1];
                int Pos(object ep,string prop)=>(int)ep.GetType().GetProperty(prop)!.GetValue(ep)!;
                original.Add(new NativeCodeVersionComparer.DocumentWhitespaceSpan(
                    Pos(oldEp,"CharStart"),Pos(oldEp,"CharEnd"),
                    Pos(newEp,"CharStart"),Pos(newEp,"CharEnd")));
            }
        }
        Check(native!.Eligible==(original.Count!=0) &&
              native.Spans.SequenceEqual(original),
              "Rust document whitespace range differs from the original C# judgment: "+
              System.Text.Json.JsonSerializer.Serialize(new{oldText,newText,managed=original,native}));
        if(native.Eligible)eligible++;
    }
    Check(eligible>100 && BeforeMatch()>startingMatches,
          "live document engine must actually accept matching Rust whitespace spans");
    Check(BeforeMismatch()==startingMismatches,
          "every eligible document whitespace comparison must match C# before replacement");
    // Malformed surrogate is not UTF-8, so the optional Rust port must not
    // break the long-established managed document comparison behavior.
    var badOld="\uD800동의없이";
    var badNew="\uD800동의 없이";
    var bad=whitespaceMethod.Invoke(null,
        new object[]{badOld,badNew,0,0,0,1,"A-B",0,"body",0});
    Check(bad is System.Collections.IEnumerable &&
          ((System.Collections.IEnumerable)bad).Cast<object>().Any(),
          "invalid UTF-16 must safely retain the original C# whitespace decision");
    Check(BeforeMismatch()==startingMismatches+1,
          "native-encoding parity fallback must be observable, not silently accepted");
    Console.WriteLine($"PASS RUST DOCUMENT WHITESPACE PARITY {corpus.Count}/{corpus.Count}, eligible={eligible}");
}

// 173. Structural 항/호 parsing parity against the *unmodified* C# parser.
// Compare label, all UTF-16 boundaries and source core, not just part counts.
if (NativeCodeVersionComparer.IsAvailable)
{
    var originalParser=typeof(NativeComparisonEngine).GetMethod(
        "ParsePartsCSharp",BindingFlags.Static|BindingFlags.NonPublic)!;
    var activeParser=typeof(NativeComparisonEngine).GetMethod(
        "ParseParts",BindingFlags.Static|BindingFlags.NonPublic)!;
    var matchProp=typeof(NativeComparisonEngine).GetProperty(
        "PartsRustParityMatches",BindingFlags.Static|BindingFlags.NonPublic)!;
    var mismatchProp=typeof(NativeComparisonEngine).GetProperty(
        "PartsRustParityMismatches",BindingFlags.Static|BindingFlags.NonPublic)!;
    long MatchCount()=>(long)matchProp.GetValue(null)!;
    long MismatchCount()=>(long)mismatchProp.GetValue(null)!;
    var startMatches=MatchCount();
    var startMismatches=MismatchCount();
    List<NativeCodeVersionComparer.DocumentPart> Canonical(string content,string parser)
    {
        var method=parser=="C#" ? originalParser : activeParser;
        var objects=(System.Collections.IEnumerable)method.Invoke(null,new object[]{content})!;
        var output=new List<NativeCodeVersionComparer.DocumentPart>();
        foreach(var o in objects.Cast<object>())
        {
            var t=o.GetType();
            T Prop<T>(string name)=>(T)t.GetProperty(name)!.GetValue(o)!;
            output.Add(new NativeCodeVersionComparer.DocumentPart(
                Prop<string>("Label"),Prop<int>("Start"),Prop<int>("End"),
                Prop<int>("CoreStart"),Prop<int>("CoreEnd"),Prop<string>("Core")));
        }
        return output;
    }
    var samples=new List<string>
    {
        "", " ", "\n", "본문만 존재한다.", "😀이용자의 동의없이 제공하지 않는다.",
        "제5조(개인정보 처리)\n① 동의 없이 처리할 수 없다.\n② 제공하면 안 된다.",
        "제5조(개인정보 처리)\n① 동의 없이 처리할 수 없다.\n② 제공하면 안 된다.\n③ 파기한다.",
        "① 첫 번째 항\n② 두 번째 항", "① 내용\r\n② 다음 항\r\n",
        "(1) First item\n(2) Second item\n(3) Third item",
        "(1) Company A (2) Company B (3) Company C",
        "Article 5 (1) may require approval.",
        "Company Policy (1) Section (2) Member",
        "This is a sentence. (1) First (2) Second (3) Third",
        "다음과 같다. (1) 하나 (2) 둘 (3) 셋",
        "제1항에 의한다.  (1) 첫째  (2) 둘째",
        "항을 열거한다.\n(가) 내용\n(나) 내용",
        "가. 권리\n나. 의무\n다. 책임",
        "1. 본문\n2. 본문\n3. 본문",
        "a. ABC\nb. DEF\nc. GHI",
        "(i) Roman a (ii) Roman b (iii) Roman c",
        "(i) Apple\n(ii) Banana\n(iii) Carrot",
        "(v) First\n(vi) Second\n(vii) Third",
        "Ⅰ. 조문 ① a\n② b",
        "\u200B① 숨은 문자 포함\n\u200C② 다음 문자 포함",
        "\uFEFF(1) 첫째\n\u2060(2) 둘째",
        "   (1) 가나다\n   (2) 라마바",
        "\t① 내용\n\t② 내용",
        "①\u00A0내용\n②\u3000내용",
        "① 본문 앞에 😀\n② 별도\n③ 두 줄\n참조",
        "① text   (2) something   (3) more",
        "본문\n\n(1) 첫째\n(2) 둘째\n",
        "① 가나다\n  ② 라마바\n    ③ 사아자",
        "① 라벨에만\n① 같은 라벨 반복\n② 다음",
        "① 제1호\n1. 항목\n가. 하위",
        "① 내용\u2028② 새줄 아닌 구분자",
        "① 내용\n⑳ 마지막",
        "\r\n1. 아이템\r\n2. 아이템\r\n",
        "①\t탭\n②\t탭2",
        "(1) Follows. (2) Next. (3) Last",
        "Text  (i) One  (ii) Two  (iii) Three",
        "🙂안내문\n(1) 가나다\n(2) 나바사\n(3) 아자차",
        " 0. 제로\n 1. 하나",
        "가) 가나다\n나) 라마바",
        "A) Apple\nB) Banana",
        "(IV) Alpha\n(V) Beta",
        "(IIX) invalid numeral (III) valid numeral (IV) valid numeral",
        "Items. (I) One (II) Two (III) Three",
        "Legal conditions: (I) Terms (II) Provisions (III) Remedies",
        "① \u2063Hidden tag\n② Follow",
        "\u200D① Item one\n\u200D② Item two",
        "١. Arabic one\n٢. Arabic two",
        "１. Fullwidth one\n２. Fullwidth two",
        "① First.  ② Second.  ③ Third",
        "Intro text\r\n\uFEFF① Part\r\n\u200C② Part",
    };
    var rand=new Random(20261022);
    string[] structuralLabels={"①","②","③","(1)","(2)","(3)","가.","나.","다.","1.","2.","3.","(a)","(b)","(i)","(ii)","(iii)","a.","b.","c."};
    string[] cores={"동의없이","동의 없이","개인정보 보호","Article 8 (1)","개인정보 처리","영업비밀","새로운 내용", "정기감사", "😀 개인정보","고객","제2조"};
    string[] seps={"\n","\r\n"," ","  ","\t","\n  ","\n\u200b","\n\n","\u3000"};
    for(int i=0;i<400;i++)
    {
        var count=rand.Next(1,7);
        var input=rand.Next(2)==0?"서문 안내\n":"";
        for(var j=0;j<count;j++)
            input+=seps[rand.Next(seps.Length)]+structuralLabels[rand.Next(structuralLabels.Length)]+" "+
                cores[rand.Next(cores.Length)];
        samples.Add(input);
    }
    var goldenPath=Path.Combine(AppContext.BaseDirectory,"Fixtures","document_parts_golden.json");
    using(var saved=System.Text.Json.JsonDocument.Parse(File.ReadAllText(goldenPath)))
        foreach(var caseValue in saved.RootElement.EnumerateArray())
        {
            var input=caseValue.GetProperty("input").GetString() ?? "";
            var json=caseValue.GetProperty("parts").GetRawText();
            var expected=System.Text.Json.JsonSerializer.Deserialize<
                List<NativeCodeVersionComparer.DocumentPart>>(json,
                new System.Text.Json.JsonSerializerOptions {
                    PropertyNameCaseInsensitive=true })!;
            Check(expected.SequenceEqual(Canonical(input,"C#")),
                "C# structural parser has regressed against committed Golden fixture: "+input);
            Check(expected.SequenceEqual(NativeCodeVersionComparer.ParseDocumentParts(input)
                    ?? Array.Empty<NativeCodeVersionComparer.DocumentPart>()),
                "Rust structural parser differs from committed Golden fixture: "+input);
        }
    samples.Add("① "+new string('한',20000)+"\n② 다음 조항");
    var mismatches=new List<string>();
    foreach(var input in samples)
    {
        var expected=Canonical(input,"C#");
        var actual=NativeCodeVersionComparer.ParseDocumentParts(input);
        var live=Canonical(input,"live");
        if(actual is null || !expected.SequenceEqual(actual) || !expected.SequenceEqual(live))
        {
            if(mismatches.Count<12)
                mismatches.Add(System.Text.Json.JsonSerializer.Serialize(new {
                    input,expected,actual,live}));
        }
    }
    if(mismatches.Count!=0)
        Console.WriteLine("DOCUMENT_PARTS_MISMATCH_DIAGNOSTIC "+
            string.Join("\nDOCUMENT_PARTS_MISMATCH_DIAGNOSTIC ",mismatches));
    Check(mismatches.Count==0,
        "Rust document part parser must agree with original managed parser on all supported fixtures");
    Check(MatchCount()-startMatches==samples.Count &&
          MismatchCount()==startMismatches,
        "live document parser must use Rust only after exact structural parity");
    var invalid="\uD800① 동의없이\n② 다음";
    var badCanonical=Canonical(invalid,"C#");
    var badLive=Canonical(invalid,"live");
    Check(badCanonical.SequenceEqual(badLive) &&
          MismatchCount()==startMismatches+1,
          "invalid UTF-16 in structure parser must preserve canonical C# behavior");
    Console.WriteLine($"PASS RUST DOCUMENT PARTS CSHARP GOLDEN PARITY {samples.Count}/{samples.Count}");
}

// 174. Rust parity for hierarchy topology, contextual Roman classification,
// and logical ordinal values, derived from the original C# parser's parts.
if(NativeCodeVersionComparer.IsAvailable)
{
    var parser=typeof(NativeComparisonEngine).GetMethod(
        "ParsePartsCSharp",BindingFlags.Static|BindingFlags.NonPublic)!;
    var csharpHierarchy=typeof(NativeComparisonEngine).GetMethod(
        "BuildPartHierarchyCSharp",BindingFlags.Static|BindingFlags.NonPublic)!;
    var liveHierarchy=typeof(NativeComparisonEngine).GetMethod(
        "BuildPartHierarchy",BindingFlags.Static|BindingFlags.NonPublic)!;
    var ordinalMethod=typeof(NativeComparisonEngine).GetMethod(
        "PartOrdinalAt",BindingFlags.Static|BindingFlags.NonPublic)!;
    var matchProperty=typeof(NativeComparisonEngine).GetProperty(
        "HierarchyRustParityMatches",BindingFlags.Static|BindingFlags.NonPublic)!;
    var mismatchProperty=typeof(NativeComparisonEngine).GetProperty(
        "HierarchyRustParityMismatches",BindingFlags.Static|BindingFlags.NonPublic)!;
    var startingMatches=(long)matchProperty.GetValue(null)!;
    var startingMismatches=(long)mismatchProperty.GetValue(null)!;
    var hierarchyCases=new List<string>();
    using(var goldenFile=System.Text.Json.JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory,"Fixtures","document_parts_golden.json"))))
    {
        hierarchyCases.AddRange(goldenFile.RootElement.EnumerateArray()
            .Select(x=>x.GetProperty("input").GetString() ?? ""));
    }
    var hierarchyRandom=new Random(20261022);
    string[] labelsForHierarchy = {"①","②","③","④","⑤","1.","2.","3.","(1)","(2)","(3)",
        "가.","나.","다.","(a)","(b)","(i)","(ii)","(iii)","(iv)","(v)",
        "(IIX)","(III)","A.","B.","c.","가)","나)","1)","2)","⑳","본문"};
    for(var round=0;round<600;round++)
    {
        var count=hierarchyRandom.Next(1,36);
        var content=string.Join("\n",Enumerable.Range(0,count).Select(_=>
            labelsForHierarchy[hierarchyRandom.Next(labelsForHierarchy.Length)]+" "+
            "개인정보 처리에 관한 내용"));
        hierarchyCases.Add(content);
    }
    hierarchyCases.Add(string.Join("\n",Enumerable.Range(0,320)
        .Select(i=>$"① 항목 {i:D3}")));
    foreach(var content in hierarchyCases)
    {
        var parts=parser.Invoke(null,new object[]{content})!;
        var structuralCaseLabels=((System.Collections.IEnumerable)parts).Cast<object>()
            .Select(p=>(string)p.GetType().GetProperty("Label")!.GetValue(p)!).ToArray();
        var actual=NativeCodeVersionComparer.AnalyzeDocumentHierarchy(structuralCaseLabels);
        Check(actual is not null && actual.Count==structuralCaseLabels.Length,
            "Rust hierarchy must return one node per part");
        var expected=((System.Collections.IEnumerable)csharpHierarchy.Invoke(null,new[]{parts})!)
            .Cast<object>().ToList();
        var live=((System.Collections.IEnumerable)liveHierarchy.Invoke(null,new[]{parts})!)
            .Cast<object>().ToList();
        Check(expected.Count==actual!.Count && live.Count==actual.Count,
            "C# and Rust structural hierarchy node counts differ");
        for(var i=0;i<expected.Count;i++)
        {
            var left=expected[i];var right=actual[i];
            var t=left.GetType();
            var idx=(int)t.GetProperty("Index")!.GetValue(left)!;
            var level=(int)t.GetProperty("Level")!.GetValue(left)!;
            var parent=(int)t.GetProperty("Parent")!.GetValue(left)!;
            var family=(string)t.GetProperty("Family")!.GetValue(left)!;
            var ordinal=(int?)ordinalMethod.Invoke(null,new object[]{parts,i});
            Check(idx==right.Index && level==right.Level &&
                  parent==right.Parent && family==right.Family &&
                  ordinal==right.Ordinal && left.Equals(live[i]),
                "Rust hierarchy differs from C#: "+System.Text.Json.JsonSerializer.Serialize(
                    new{content,structuralCaseLabels,index=i,idx,level,parent,family,ordinal,rust=right}));
        }
    }
    Check((long)matchProperty.GetValue(null)!-startingMatches==hierarchyCases.Count &&
          (long)mismatchProperty.GetValue(null)! == startingMismatches,
          "live hierarchy path must only accept exactly equivalent Rust nodes");
    Console.WriteLine($"PASS RUST DOCUMENT HIERARCHY CSHARP PARITY {hierarchyCases.Count}/{hierarchyCases.Count}");
}

// 175. MatchParts first pass: exact normalized body pairing, including
// insertion-before-renumbering and nearest unused duplicate candidate.
if (NativeCodeVersionComparer.IsAvailable)
{
    var owner=typeof(NativeComparisonEngine);
    var reference=owner.GetMethod("MatchExactPartKeysCSharp",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var liveStage=owner.GetMethod("MatchExactPartKeys",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var parseOriginal=owner.GetMethod("ParsePartsCSharp",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var matchWhole=owner.GetMethod("MatchParts",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var normalize=owner.GetMethod("LineageNormalize",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var countMatch=owner.GetProperty("ExactPartsRustParityMatches",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var countMismatch=owner.GetProperty("ExactPartsRustParityMismatches",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    long M()=>(long)countMatch.GetValue(null)!;
    long E()=>(long)countMismatch.GetValue(null)!;
    var initialMatch=M();
    var initialMismatch=E();
    List<NativeCodeVersionComparer.ExactDocumentPartPair> Ref(string[] oldKeys,string[] newKeys,
        System.Reflection.MethodInfo method) =>
        ((System.Collections.IEnumerable)method.Invoke(null,new object[]{oldKeys,newKeys})!)
        .Cast<NativeCodeVersionComparer.ExactDocumentPartPair>().ToList();

    var handCases=new List<(string[],string[])>
    {
        (new[]{"가","나","다"},new[]{"가","새항목","나","다"}),
        (new[]{"공통","공통","공통"},new[]{"공통","교체","공통","공통"}),
        (new[]{"",""},new[]{"",""}),
        (new[]{"한글😀","동의없이",""},new[]{"","동의없이","한글😀"}),
        (new[]{"same","same",""},new[]{"same","same"}),
        (new[]{"a","b","c"},new[]{"c","b","a"}),
        (Array.Empty<string>(),Array.Empty<string>()),
        (new[]{"abc"},Array.Empty<string>()),
        (Array.Empty<string>(),new[]{"abc"}),
        (new[]{"same","other","same","other"},
            new[]{"other","same","other","same"})
    };
    // Exact C# stage tie/greedy contracts are fixed in the fixture, not
    // inferred from coincidental similarity of the final heuristic matcher.
    var exactMovedPairs = new[]{
        new NativeCodeVersionComparer.ExactDocumentPartPair(0,0),
        new NativeCodeVersionComparer.ExactDocumentPartPair(1,2),
        new NativeCodeVersionComparer.ExactDocumentPartPair(2,3)
    };
    Check(Ref(handCases[0].Item1,handCases[0].Item2,reference).SequenceEqual(exactMovedPairs),
        "C# Golden contract: inserted new 2. moves old 2. and 3. to 3. and 4.");
    using (var frozen=System.Text.Json.JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory,"Fixtures","document_match_exact_golden.json"))))
    {
        foreach (var fixture in frozen.RootElement.EnumerateArray())
        {
            var before=fixture.GetProperty("before").EnumerateArray()
                .Select(x=>x.GetString()!).ToArray();
            var after=fixture.GetProperty("after").EnumerateArray()
                .Select(x=>x.GetString()!).ToArray();
            var expected=fixture.GetProperty("pairs").EnumerateArray()
                .Select(x=>new NativeCodeVersionComparer.ExactDocumentPartPair(
                    x.GetProperty("old").GetInt32(),x.GetProperty("new").GetInt32())).ToArray();
            Check(Ref(before,after,reference).SequenceEqual(expected),
                "Golden fixture vs original C# exact MatchParts stage drift");
            Check(expected.SequenceEqual(
                    NativeCodeVersionComparer.MatchExactDocumentParts(before,after)
                    ?? Array.Empty<NativeCodeVersionComparer.ExactDocumentPartPair>()),
                "Golden fixture vs Rust exact MatchParts stage drift");
        }
    }
    var exactRandom=new Random(20261023);
    string[] words={"","동의없이","동의 없이","personaldata","한글","😀","☃",
        "조항A","서문","본문","배상책임","같은본문","Aa","aA","Ａ",
        "abc","x","y","123","§","<문자>"};
    for(var turn=0;turn<700;turn++)
    {
        var before=Enumerable.Range(0,exactRandom.Next(0,42))
            .Select(_=>words[exactRandom.Next(words.Length)]).ToArray();
        var after=Enumerable.Range(0,exactRandom.Next(0,42))
            .Select(_=>words[exactRandom.Next(words.Length)]).ToArray();
        handCases.Add((before,after));
    }
    foreach(var (before,after) in handCases)
    {
        var expected=Ref(before,after,reference);
        var actual=NativeCodeVersionComparer.MatchExactDocumentParts(before,after);
        Check(actual is not null && expected.SequenceEqual(actual),
            "Rust and C# MatchParts exact stage changed index/duplicate matching: "+
            System.Text.Json.JsonSerializer.Serialize(new{before,after,expected,actual}));
        Check(Ref(before,after,liveStage).SequenceEqual(expected),
            "live MatchParts exact stage should use only fully matched Rust output");
    }
    Check(M()-initialMatch==handCases.Count && E()==initialMismatch,
        "live exact-stage match counter must cover all direct cases without fallback");

    var tooMany=Enumerable.Repeat("a",257).ToArray();
    Check(NativeCodeVersionComparer.MatchExactDocumentParts(tooMany,tooMany) is null &&
          Ref(tooMany,tooMany,liveStage).SequenceEqual(Ref(tooMany,tooMany,reference)),
          "oversized document item list must retain C# fallback");

    var baseline=M();
    var oldText="① 개인정보 수집 목적 안내\n② 개인정보 이용 범위 안내\n③ 개인정보 파기 기준 안내";
    var newText="① 개인정보 수집 목적 안내\n② 신규 개인정보 보관 기준 안내\n③ 개인정보 이용 범위 안내\n④ 개인정보 파기 기준 안내";
    var oldParts=parseOriginal.Invoke(null,new object[]{oldText})!;
    var newParts=parseOriginal.Invoke(null,new object[]{newText})!;
    var oldNormalized=((System.Collections.IEnumerable)oldParts).Cast<object>()
        .Select(x=>(string)normalize.Invoke(null,new object[]{
            (string)x.GetType().GetProperty("Core")!.GetValue(x)!})!).ToArray();
    var newNormalized=((System.Collections.IEnumerable)newParts).Cast<object>()
        .Select(x=>(string)normalize.Invoke(null,new object[]{
            (string)x.GetType().GetProperty("Core")!.GetValue(x)!})!).ToArray();
    Check(Ref(oldNormalized,newNormalized,reference).SequenceEqual(exactMovedPairs),
        "real legal items must match old ②→new ③, old ③→new ④ before heuristic fallback");
    _=matchWhole.Invoke(null,new[]{oldParts,newParts});
    Check(M()>baseline && E()==initialMismatch,
        "real MatchParts must use parity-gated Rust exact-body stage");
    Console.WriteLine($"PASS RUST MATCHPARTS EXACT BODY CSHARP PARITY {handCases.Count}/{handCases.Count}");
}

// 176. Modified document item moving to a different number: same C# score,
// C# rank/priority ordering, and Rust FFI greedy candidate pairing.
if (NativeCodeVersionComparer.IsAvailable)
{
    var type=typeof(NativeComparisonEngine);
    var reference=type.GetMethod("SelectPartMoveCandidatesCSharp",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var liveSelector=type.GetMethod("SelectPartMoveCandidates",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var originalParser=type.GetMethod("ParsePartsCSharp",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var actualMatch=type.GetMethod("MatchParts",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var matchCount=type.GetProperty("MoveCandidatesRustParityMatches",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var mismatchCount=type.GetProperty("MoveCandidatesRustParityMismatches",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    long Matches()=>(long)matchCount.GetValue(null)!;
    long Mismatches()=>(long)mismatchCount.GetValue(null)!;
    var baseMatches=Matches();
    var baseMismatches=Mismatches();
    List<NativeCodeVersionComparer.DocumentMoveSelected> Invoke(
        System.Reflection.MethodInfo method, int oldCount,int newCount,
        List<NativeCodeVersionComparer.DocumentMoveCandidate> candidates) =>
        ((System.Collections.IEnumerable)method.Invoke(
            null,new object[]{oldCount,newCount,candidates})!)
            .Cast<NativeCodeVersionComparer.DocumentMoveSelected>().ToList();

    var insertedModified=new List<NativeCodeVersionComparer.DocumentMoveCandidate> {
        new(2,2,.10,.10,0), new(2,3,.93,.95,0),
        new(1,1,.10,.10,0), new(1,2,.90,.90,0),
    };
    var golden=Invoke(reference,3,4,insertedModified);
    Check(golden.Select(p=>(p.Old,p.New)).SequenceEqual(
        new[]{(2,3),(1,2)}),
        "original C# modified-move Golden must prefer content over matching enumerator");
    using (var frozen=System.Text.Json.JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory,"Fixtures","document_move_candidates_golden.json"))))
    {
        foreach (var fixture in frozen.RootElement.EnumerateArray())
        {
            var oldCount=fixture.GetProperty("oldCount").GetInt32();
            var newCount=fixture.GetProperty("newCount").GetInt32();
            var source=fixture.GetProperty("candidates").GetRawText();
            var candidates=System.Text.Json.JsonSerializer.Deserialize<
                List<NativeCodeVersionComparer.DocumentMoveCandidate>>(source,
                new System.Text.Json.JsonSerializerOptions {
                    PropertyNameCaseInsensitive=true })!;
            var pairs=fixture.GetProperty("selected").EnumerateArray()
                .Select(x=>(Old:x.GetProperty("old").GetInt32(),
                    New:x.GetProperty("new").GetInt32())).ToArray();
            var csharp=Invoke(reference,oldCount,newCount,candidates);
            var rust=NativeCodeVersionComparer.SelectDocumentPartMoves(
                oldCount,newCount,candidates)!;
            Check(csharp.Select(x=>(x.Old,x.New)).SequenceEqual(pairs) &&
                rust.Select(x=>(x.Old,x.New)).SequenceEqual(pairs) &&
                csharp.Count==rust.Count &&
                csharp.Zip(rust).All(x=>Math.Abs(x.First.Score-x.Second.Score)<1e-12),
                "Modified-move Golden candidate ranking regression: "+
                fixture.GetRawText());
        }
    }
    var random=new Random(20261023);
    var candidateCases=0;
    for(var round=0;round<900;round++)
    {
        var oldCount=random.Next(0,34);
        var newCount=random.Next(0,34);
        var candidates=new List<NativeCodeVersionComparer.DocumentMoveCandidate>();
        for(var i=0;i<oldCount;i++)
            for(var j=0;j<newCount;j++)
                if(random.NextDouble()<.12)
                {
                    var similarities=new[]{0.0,.2,.5499,.55,.7399,.74,.85,1.0};
                    var contains=new[]{0.0,.3,.8799,.88,1.0};
                    candidates.Add(new(i,j,
                        similarities[random.Next(similarities.Length)],
                        contains[random.Next(contains.Length)],
                        random.Next(0,4)));
                }
        if(round==0) candidates=insertedModified;
        var expected=Invoke(reference,oldCount:round==0?3:oldCount,
            newCount:round==0?4:newCount,candidates);
        var resolvedOld=round==0?3:oldCount;
        var resolvedNew=round==0?4:newCount;
        var rust=NativeCodeVersionComparer.SelectDocumentPartMoves(
            resolvedOld,resolvedNew,candidates);
        Check(rust is not null && expected.Count==rust.Count &&
            expected.Zip(rust).All(x=>x.First.Old==x.Second.Old &&
                x.First.New==x.Second.New &&
                Math.Abs(x.First.Score-x.Second.Score)<1e-12),
            "Rust changed-move matching differs from C# baseline: "+round);
        var used=Invoke(liveSelector,resolvedOld,resolvedNew,candidates);
        Check(expected.Count==used.Count &&
            expected.Zip(used).All(x=>x.First.Old==x.Second.Old &&
                x.First.New==x.Second.New &&
                Math.Abs(x.First.Score-x.Second.Score)<1e-12),
            "live C# document engine must only accept exact Rust move candidates");
        candidateCases++;
    }
    Check(Matches()-baseMatches==candidateCases &&
        Mismatches()==baseMismatches,
        "candidate stage must have zero parity divergence");

    var oldDoc="① 개인정보 수집 목적 안내\n② 개인정보 이용 범위 안내\n"+
        "③ 이용자의 개인정보는 이용 목적이 완료되면 지체 없이 안전한 방법으로 삭제하고 기록을 보존한다.";
    var newDoc="① 개인정보 수집 목적 안내\n② 신규 개인정보 보관 기준 안내\n"+
        "③ 개인정보 이용 범위 안내\n"+
        "④ 이용자의 개인정보는 이용 목적이 완료되면 지체 없이 안전한 방식으로 삭제하고 기록을 보존한다.";
    var oldParts=originalParser.Invoke(null,new object[]{oldDoc})!;
    var newParts=originalParser.Invoke(null,new object[]{newDoc})!;
    var beforeLive=Matches();
    var matched=((System.Collections.IEnumerable)actualMatch.Invoke(
        null,new[]{oldParts,newParts})!).Cast<object>().ToList();
    var matchedPairs=matched.Select(x=>(
        Old:(int)x.GetType().GetProperty("Old")!.GetValue(x)!,
        New:(int)x.GetType().GetProperty("New")!.GetValue(x)!)).ToArray();
    Check(matchedPairs.Contains((2,3)) &&
        !matchedPairs.Contains((2,2)) &&
        Matches()>beforeLive,
        "real document matching must find edited old ③→new ④, not old ③→new ③");
    Check(Mismatches()==baseMismatches,
        "real modified move must not create native-vs-managed parity divergence");
    Console.WriteLine($"PASS RUST MODIFIED MOVE CANDIDATE CSHARP PARITY {candidateCases}/{candidateCases}");
}

// 177. C#↔Rust same-label sibling-shape fallback, including complete
// wording changes, greedy duplicate matches, tie ordering and thresholds.
if (NativeCodeVersionComparer.IsAvailable)
{
    var sameOwner=typeof(NativeComparisonEngine);
    var sameReference=sameOwner.GetMethod("SelectSameLabelCandidatesCSharp",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var sameLive=sameOwner.GetMethod("SelectSameLabelCandidates",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var sameParser=sameOwner.GetMethod("ParsePartsCSharp",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var sameMatcher=sameOwner.GetMethod("MatchParts",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var sameMatchCounter=sameOwner.GetProperty("SameLabelRustParityMatches",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var sameMismatchCounter=sameOwner.GetProperty("SameLabelRustParityMismatches",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    long SameMatches()=>(long)sameMatchCounter.GetValue(null)!;
    long SameMismatches()=>(long)sameMismatchCounter.GetValue(null)!;
    var sameMatchStart=SameMatches();
    var sameMismatchStart=SameMismatches();

    List<NativeCodeVersionComparer.DocumentMoveSelected> SameRun(
        System.Reflection.MethodInfo method,int oldCount,int newCount,
        List<NativeCodeVersionComparer.DocumentSameLabelCandidate> candidates)=>
        ((System.Collections.IEnumerable)method.Invoke(
            null,new object[]{oldCount,newCount,candidates})!)
        .Cast<NativeCodeVersionComparer.DocumentMoveSelected>().ToList();

    using(var fixedCases=System.Text.Json.JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory,"Fixtures","document_same_label_golden.json"))))
        foreach(var item in fixedCases.RootElement.EnumerateArray())
        {
            var before=item.GetProperty("oldCount").GetInt32();
            var after=item.GetProperty("newCount").GetInt32();
            var candidates=System.Text.Json.JsonSerializer.Deserialize<
                List<NativeCodeVersionComparer.DocumentSameLabelCandidate>>(
                    item.GetProperty("candidates").GetRawText(),
                    new System.Text.Json.JsonSerializerOptions{
                        PropertyNameCaseInsensitive=true})!;
            var gold=item.GetProperty("selected").EnumerateArray().Select(x=>
                new NativeCodeVersionComparer.DocumentMoveSelected(
                    x.GetProperty("old").GetInt32(),
                    x.GetProperty("new").GetInt32(),
                    x.GetProperty("score").GetDouble())).ToArray();
            var cs=SameRun(sameReference,before,after,candidates);
            var rust=NativeCodeVersionComparer.SelectSameLabelDocumentParts(
                before,after,candidates);
            Check(rust is not null && cs.SequenceEqual(gold) &&
                  rust.SequenceEqual(gold),
                "committed C# same-label Golden changed or differs in Rust: "+
                item.GetRawText());
        }

    var sameRandom=new Random(20261023);
    var sameCases=0;
    for(var iteration=0;iteration<850;iteration++)
    {
        var oldCount=sameRandom.Next(0,31);
        var newCount=sameRandom.Next(0,31);
        var candidates=new List<NativeCodeVersionComparer.DocumentSameLabelCandidate>();
        var values=new[]{0.0,.1,.1799,.18,.3,.51,.52,.8,1.0};
        for(var old=0;old<oldCount;old++)
            for(var next=0;next<newCount;next++)
                if(sameRandom.NextDouble()<.14)
                    candidates.Add(new(old,next,
                        values[sameRandom.Next(values.Length)],
                        sameRandom.NextDouble()<.22,
                        sameRandom.NextDouble()<.17));
        var canonical=SameRun(sameReference,oldCount,newCount,candidates);
        var rust=NativeCodeVersionComparer.SelectSameLabelDocumentParts(
            oldCount,newCount,candidates);
        Check(rust is not null && canonical.Count==rust.Count &&
              canonical.Zip(rust).All(pair=>
                  pair.First.Old==pair.Second.Old &&
                  pair.First.New==pair.Second.New &&
                  Math.Abs(pair.First.Score-pair.Second.Score)<1e-12),
              "same-label selection drift from C# baseline: "+iteration);
        var active=SameRun(sameLive,oldCount,newCount,candidates);
        Check(active.Count==canonical.Count &&
              active.Zip(canonical).All(pair=>
                  pair.First.Old==pair.Second.Old &&
                  pair.First.New==pair.Second.New &&
                  Math.Abs(pair.First.Score-pair.Second.Score)<1e-12),
              "live same-label stage must be fully parity-gated: "+iteration);
        sameCases++;
    }
    Check(SameMatches()-sameMatchStart==sameCases &&
          SameMismatches()==sameMismatchStart,
          "same-label parity gate must accept every deterministic paired input");

    var unchangedLabelsOld="① 고객정보를 최초 수집하는 경우 이용목적과 보유기간을 명확하게 안내한다.\n"+
        "② 사전 승인 절차를 완료한 처리자에게만 시스템 접근 권한을 부여한다.\n"+
        "③ 이용자는 본인의 개인정보에 관한 열람과 정정을 언제든지 요청할 수 있다.";
    var unchangedLabelsNew="① 연구개발팀은 제품을 월별 기준으로 테스트한다.\n"+
        "② 경영진은 다음 분기 자산의 목표 실적을 수립한다.\n"+
        "③ 영업팀은 해외 협력업체와 신규 공급 계약을 체결한다.";
    var sourceParts=sameParser.Invoke(null,new object[]{unchangedLabelsOld})!;
    var changedParts=sameParser.Invoke(null,new object[]{unchangedLabelsNew})!;
    var beforeMatch=SameMatches();
    var matched=((System.Collections.IEnumerable)sameMatcher.Invoke(
        null,new[]{sourceParts,changedParts})!).Cast<object>().ToList();
    var pairs=matched.Select(value=>(
        Old:(int)value.GetType().GetProperty("Old")!.GetValue(value)!,
        New:(int)value.GetType().GetProperty("New")!.GetValue(value)!)).ToArray();
    Check(pairs.Contains((0,0)) && pairs.Contains((1,1)) && pairs.Contains((2,2)) &&
          SameMatches()>beforeMatch && SameMismatches()==sameMismatchStart,
          "rewritten ①②③ must stay aligned by stable sibling structure");
    var maxSize=Enumerable.Range(0,257).Select(i=>
        new NativeCodeVersionComparer.DocumentSameLabelCandidate(i,0,.8,true,false)).ToList();
    Check(NativeCodeVersionComparer.SelectSameLabelDocumentParts(257,1,maxSize) is null &&
          SameRun(sameLive,257,1,maxSize)
              .SequenceEqual(SameRun(sameReference,257,1,maxSize)),
          "same-label stage must fall back to C# beyond native item limit");
    Console.WriteLine($"PASS RUST SAME-LABEL SIBLING SHAPE CSHARP PARITY {sameCases}/{sameCases}");
}

// 178. Same logical ordinal but different notation: ①↔1., (1)↔1.
// Scores, ranks, shape precedence, thresholds, duplicates and greedy order
// must exactly match the original C# stage before selecting Rust output.
if (NativeCodeVersionComparer.IsAvailable)
{
    var ordinalType=typeof(NativeComparisonEngine);
    var ordinalReference=ordinalType.GetMethod("SelectSameOrdinalCandidatesCSharp",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var ordinalLive=ordinalType.GetMethod("SelectSameOrdinalCandidates",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var ordinalParser=ordinalType.GetMethod("ParsePartsCSharp",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var ordinalMatch=ordinalType.GetMethod("MatchParts",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var ordinalMatchesProperty=ordinalType.GetProperty("SameOrdinalRustParityMatches",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var ordinalMismatchesProperty=ordinalType.GetProperty("SameOrdinalRustParityMismatches",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    long OrdinalMatches()=>(long)ordinalMatchesProperty.GetValue(null)!;
    long OrdinalMismatches()=>(long)ordinalMismatchesProperty.GetValue(null)!;
    var ordinalInitialMatches=OrdinalMatches();
    var ordinalInitialMismatches=OrdinalMismatches();

    List<NativeCodeVersionComparer.DocumentMoveSelected> SelectOrdinal(
        System.Reflection.MethodInfo method,int beforeCount,int afterCount,
        List<NativeCodeVersionComparer.DocumentSameOrdinalCandidate> candidates) =>
        ((System.Collections.IEnumerable)method.Invoke(null,
            new object[]{beforeCount,afterCount,candidates})!)
            .Cast<NativeCodeVersionComparer.DocumentMoveSelected>().ToList();

    using (var goldenOrdinal=System.Text.Json.JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory,"Fixtures","document_same_ordinal_golden.json"))))
        foreach (var fixture in goldenOrdinal.RootElement.EnumerateArray())
        {
            var oldCount=fixture.GetProperty("oldCount").GetInt32();
            var newCount=fixture.GetProperty("newCount").GetInt32();
            var candidates=System.Text.Json.JsonSerializer.Deserialize<
                List<NativeCodeVersionComparer.DocumentSameOrdinalCandidate>>(
                    fixture.GetProperty("candidates").GetRawText(),
                    new System.Text.Json.JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
            var expected=fixture.GetProperty("selected").EnumerateArray().Select(x=>
                new NativeCodeVersionComparer.DocumentMoveSelected(
                    x.GetProperty("old").GetInt32(),x.GetProperty("new").GetInt32(),
                    x.GetProperty("score").GetDouble())).ToArray();
            var managed=SelectOrdinal(ordinalReference,oldCount,newCount,candidates);
            var rust=NativeCodeVersionComparer.SelectSameOrdinalDocumentParts(
                oldCount,newCount,candidates);
            Check(rust is not null && managed.Count==expected.Length &&
                  rust.Count==expected.Length &&
                  managed.Zip(expected).All(x=>x.First.Old==x.Second.Old &&
                      x.First.New==x.Second.New &&
                      Math.Abs(x.First.Score-x.Second.Score)<1e-12) &&
                  rust.Zip(expected).All(x=>x.First.Old==x.Second.Old &&
                      x.First.New==x.Second.New &&
                      Math.Abs(x.First.Score-x.Second.Score)<1e-12),
                "committed same-ordinal C#/Rust Golden changed: "+fixture.GetRawText());
        }

    var ordinalRandom=new Random(20261023);
    var ordinalValues=new[]{0.0,.1,.1999,.20,.3499,.35,.6199,.62,.7599,.76,.9,1.0};
    var ordinalCases=0;
    for (var iteration=0;iteration<850;iteration++)
    {
        var oldCount=ordinalRandom.Next(0,31);
        var newCount=ordinalRandom.Next(0,31);
        var candidates=new List<NativeCodeVersionComparer.DocumentSameOrdinalCandidate>();
        for (var i=0;i<oldCount;i++)
        for (var j=0;j<newCount;j++)
            if (ordinalRandom.NextDouble()<.13)
                candidates.Add(new(i,j,
                    ordinalValues[ordinalRandom.Next(ordinalValues.Length)],
                    ordinalValues[ordinalRandom.Next(ordinalValues.Length)],
                    ordinalRandom.NextDouble()<.24,
                    ordinalRandom.NextDouble()<.18));
        var original=SelectOrdinal(ordinalReference,oldCount,newCount,candidates);
        var rust=NativeCodeVersionComparer.SelectSameOrdinalDocumentParts(
            oldCount,newCount,candidates);
        Check(rust is not null && original.Count==rust.Count &&
              original.Zip(rust).All(x=>
                  x.First.Old==x.Second.Old &&
                  x.First.New==x.Second.New &&
                  Math.Abs(x.First.Score-x.Second.Score)<1e-12),
            "same-ordinal matching diverged from managed baseline: "+iteration);
        var live=SelectOrdinal(ordinalLive,oldCount,newCount,candidates);
        Check(live.Count==original.Count &&
              live.Zip(original).All(x=>
                  x.First.Old==x.Second.Old &&
                  x.First.New==x.Second.New &&
                  Math.Abs(x.First.Score-x.Second.Score)<1e-12),
            "live same-ordinal selection must only adopt fully matching Rust output");
        ordinalCases++;
    }
    Check(OrdinalMatches()-ordinalInitialMatches==ordinalCases &&
          OrdinalMismatches()==ordinalInitialMismatches,
        "ordinal parity-gate must cover all direct C#/Rust cases");

    var fromOrdinalText="① 개인정보를 수집하기 전에 이용목적과 처리항목을 안내한다.\n"+
        "② 처리자가 업무상 필요한 경우 제한적으로만 자료에 접근한다.";
    var toOrdinalText="1. 개인정보를 수집하기 전에 이용목적과 보유기간을 안내한다.\n"+
        "2. 처리자가 업무상 필요한 경우 승인받은 후 자료에 접근한다.";
    var beforeOrdinalParts=ordinalParser.Invoke(null,new object[]{fromOrdinalText})!;
    var afterOrdinalParts=ordinalParser.Invoke(null,new object[]{toOrdinalText})!;
    var priorOrdinalLive=OrdinalMatches();
    var matchedOrdinal=((System.Collections.IEnumerable)ordinalMatch.Invoke(null,
        new[]{beforeOrdinalParts,afterOrdinalParts})!).Cast<object>().ToList();
    var ordinalPairs=matchedOrdinal.Select(m=>(
        Old:(int)m.GetType().GetProperty("Old")!.GetValue(m)!,
        New:(int)m.GetType().GetProperty("New")!.GetValue(m)!)).ToArray();
    Check(ordinalPairs.Contains((0,0)) && ordinalPairs.Contains((1,1)) &&
          OrdinalMatches()>priorOrdinalLive &&
          OrdinalMismatches()==ordinalInitialMismatches,
        "real ①② to 1.2. formatting and changed wording must keep ordinal pair identity");
    var oversized=Enumerable.Range(0,257).Select(x=>
        new NativeCodeVersionComparer.DocumentSameOrdinalCandidate(x,0,.9,.9,true,true)).ToList();
    Check(NativeCodeVersionComparer.SelectSameOrdinalDocumentParts(
              257,1,oversized) is null &&
          SelectOrdinal(ordinalLive,257,1,oversized)
              .SequenceEqual(SelectOrdinal(ordinalReference,257,1,oversized)),
        "same-ordinal native size limit must retain the C# selection");
    Console.WriteLine($"PASS RUST SAME-ORDINAL DRAFTING CSHARP PARITY {ordinalCases}/{ordinalCases}");
}

// 179. Adjacent-level collapse/expansion selector exact parity.
// C# remains authoritative for CollapsedLevelParentsEquivalent,
// PartOrdinalAt, PartSimilarity and HasStableEdgeContext.
if (NativeCodeVersionComparer.IsAvailable)
{
    var crossOwner = typeof(NativeComparisonEngine);
    var crossCanonical = crossOwner.GetMethod("SelectCrossLevelCandidatesCSharp",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var crossLive = crossOwner.GetMethod("SelectCrossLevelCandidates",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var crossParser = crossOwner.GetMethod("ParsePartsCSharp",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var crossMatcher = crossOwner.GetMethod("MatchParts",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var crossMatchProp = crossOwner.GetProperty("CrossLevelRustParityMatches",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var crossMismatchProp = crossOwner.GetProperty("CrossLevelRustParityMismatches",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    long CrossMatches()=>(long)crossMatchProp.GetValue(null)!;
    long CrossMismatches()=>(long)crossMismatchProp.GetValue(null)!;
    var crossMatchesStart=CrossMatches();
    var crossMismatchesStart=CrossMismatches();
    List<NativeCodeVersionComparer.DocumentMoveSelected> CrossRun(
        System.Reflection.MethodInfo method, int oldCount,int newCount,
        List<NativeCodeVersionComparer.DocumentCrossLevelCandidate> candidates) =>
        ((System.Collections.IEnumerable)method.Invoke(
            null,new object[]{oldCount,newCount,candidates})!)
            .Cast<NativeCodeVersionComparer.DocumentMoveSelected>().ToList();

    using (var frozen=System.Text.Json.JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory,"Fixtures","document_cross_level_golden.json"))))
        foreach(var fixture in frozen.RootElement.EnumerateArray())
        {
            var oldCount=fixture.GetProperty("oldCount").GetInt32();
            var newCount=fixture.GetProperty("newCount").GetInt32();
            var candidates=System.Text.Json.JsonSerializer.Deserialize<
                List<NativeCodeVersionComparer.DocumentCrossLevelCandidate>>(
                fixture.GetProperty("candidates").GetRawText(),
                new System.Text.Json.JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
            var expected=fixture.GetProperty("selected").EnumerateArray().Select(x=>
                new NativeCodeVersionComparer.DocumentMoveSelected(
                x.GetProperty("old").GetInt32(),x.GetProperty("new").GetInt32(),
                x.GetProperty("score").GetDouble())).ToArray();
            var managed=CrossRun(crossCanonical,oldCount,newCount,candidates);
            var rust=NativeCodeVersionComparer.SelectCrossLevelDocumentParts(
                oldCount,newCount,candidates);
            Check(rust is not null && expected.Length==rust.Count &&
                  expected.Length==managed.Count &&
                  managed.Zip(expected).All(x=>x.First.Old==x.Second.Old &&
                    x.First.New==x.Second.New &&
                    Math.Abs(x.First.Score-x.Second.Score)<1e-12) &&
                  rust.Zip(expected).All(x=>x.First.Old==x.Second.Old &&
                    x.First.New==x.Second.New &&
                    Math.Abs(x.First.Score-x.Second.Score)<1e-12),
                "cross-level Golden mismatch: "+fixture.GetRawText());
        }
    var crossRandom=new Random(20261023);
    var crossScores=new[]{0.0,.01,.3399,.34,.4999,.50,.7399,.74,.8599,.86,.95,1.0};
    var crossCases=0;
    for(var iteration=0;iteration<900;iteration++)
    {
        var oldCount=crossRandom.Next(0,31);
        var newCount=crossRandom.Next(0,31);
        var candidates=new List<NativeCodeVersionComparer.DocumentCrossLevelCandidate>();
        for(var i=0;i<oldCount;i++)
        for(var j=0;j<newCount;j++)
            if(crossRandom.NextDouble()<.14)
                candidates.Add(new(i,j,
                    crossScores[crossRandom.Next(crossScores.Length)],
                    crossScores[crossRandom.Next(crossScores.Length)],
                    crossRandom.NextDouble()<.5,
                    crossRandom.NextDouble()<.18));
        var managed=CrossRun(crossCanonical,oldCount,newCount,candidates);
        var rust=NativeCodeVersionComparer.SelectCrossLevelDocumentParts(
            oldCount,newCount,candidates);
        Check(rust is not null && rust.Count==managed.Count &&
              rust.Zip(managed).All(pair=>
                pair.First.Old==pair.Second.Old &&
                pair.First.New==pair.Second.New &&
                Math.Abs(pair.First.Score-pair.Second.Score)<1e-12),
            "cross-level C# vs Rust candidate mismatch: "+iteration);
        var live=CrossRun(crossLive,oldCount,newCount,candidates);
        Check(managed.Count==live.Count &&
              managed.Zip(live).All(pair=>
                pair.First.Old==pair.Second.Old &&
                pair.First.New==pair.Second.New &&
                Math.Abs(pair.First.Score-pair.Second.Score)<1e-12),
            "cross-level live fallback changed canonical selection: "+iteration);
        crossCases++;
    }
    Check(CrossMatches()-crossMatchesStart==crossCases &&
          CrossMismatches()==crossMismatchesStart,
        "cross-level parity gate must accept exact native output");
    // Actual MatchParts with one parent level dropped; ensure this new
    // Rust stage executes without creating false cross-level pairings.
    var oldCrossText="① 개인정보 처리에 관한 총괄 승인사항을 담당한다.\n"+
        "1. 이용자의 정보를 계약 이행을 위해 처리하고 요청시 상세내역을 제공한다.\n"+
        "2. 수집된 고객자료는 보유기간 종료시 신속하게 삭제하며 별도 로그를 기록한다.";
    var newCrossText="(1) 이용자의 정보를 계약 이행을 위해 처리하고 요청시 상세내역을 안내한다.\n"+
        "(2) 수집된 고객자료는 보유기간 종료시 안전하게 삭제하며 별도 로그를 기록한다.";
    var oldCrossParts=crossParser.Invoke(null,new object[]{oldCrossText})!;
    var newCrossParts=crossParser.Invoke(null,new object[]{newCrossText})!;
    var preLive=CrossMatches();
    var fullMatches=((System.Collections.IEnumerable)crossMatcher.Invoke(
        null,new[]{oldCrossParts,newCrossParts})!).Cast<object>().ToList();
    var paired=fullMatches.Select(p=>(
        Old:(int)p.GetType().GetProperty("Old")!.GetValue(p)!,
        New:(int)p.GetType().GetProperty("New")!.GetValue(p)!)).ToArray();
    Check(paired.Contains((1,0)) && paired.Contains((2,1)) &&
          !paired.Any(x=>x.Old==0 && x.New>=0) &&
          CrossMatches()>preLive && CrossMismatches()==crossMismatchesStart,
        "real collapsed parent must map former nested children only");
    var tooMany=Enumerable.Range(0,257).Select(x=>
        new NativeCodeVersionComparer.DocumentCrossLevelCandidate(x,0,.8,.8,true,true)).ToList();
    Check(NativeCodeVersionComparer.SelectCrossLevelDocumentParts(257,1,tooMany) is null &&
          CrossRun(crossLive,257,1,tooMany)
            .SequenceEqual(CrossRun(crossCanonical,257,1,tooMany)),
        "cross-level count budget must preserve managed fallback");
    Console.WriteLine($"PASS RUST COLLAPSED LEVEL CSHARP PARITY {crossCases}/{crossCases}");
}

// SCREENSHOT REGRESSION: v5.21.9 article 26 -> 27 fee paragraph.
// Two long sentences survive unchanged, one middle sentence disappears,
// and a new statutory refund sentence is appended.
var feeOldBody="회원은 회원 또는 회원에게 등록된 계정을 이용하는 누군가에 의하여 발생하는 모든 비용과 해당 세금을 지급하는 것에 동의합니다. "+
    "회원은 언제든지 서비스를 통하여 제공되는 상품과 용역의 가격을 수정할 수 있습니다. "+
    "회원은 계정이 차별적 또는 비차별적으로 폐쇄되었을 때, 미사용 가상 아이템에 관하여 현금을 수령하거나 또는 다른 배상을 받지 않는다는 점에 동의합니다.";
var feeNewBody="회원은 회원 또는 회원에게 등록된 계정을 이용하는 누군가에 의하여 발생하는 모든 비용과 해당 세금을 지급하는 것에 동의합니다. "+
    "회원은 계정이 차별적 또는 비차별적으로 폐쇄되었을 때, 미사용 가상 아이템에 관하여 현금을 수령하거나 또는 다른 배상을 받지 않는다는 점에 동의합니다. "+
    "다만, 관련 법령 및 이 약관에 따른 청약철회·환급 등 회원의 권리는 제한되지 않습니다.";
var feeOld=Path.Combine(dir,"fee-26-before.docx");
var feeNew=Path.Combine(dir,"fee-27-after.docx");
Make(feeOld,P("제26조(수수료의 지급)")+P(feeOldBody));
Make(feeNew,P("제27조(수수료의 지급)")+P(feeNewBody));
var nativeSentenceProperty=typeof(NativeComparisonEngine).GetProperty(
    "RawPairRustMatches",BindingFlags.NonPublic|BindingFlags.Static)!;
long SentenceNativeAccepted()=>(long)nativeSentenceProperty.GetValue(null)!;
var beforeFeeRustSentence=SentenceNativeAccepted();
var feeResult=await eng.CompareAsync(new[]{feeOld,feeNew},0,"legal",true,true);
Check(SentenceNativeAccepted()>beforeFeeRustSentence,
    "v5.21.9 fee article sentence diff must execute Rust-owned ComparePair");
var feeRow=feeResult.Rows.Single(r=>r.Members[0]?.Number=="26");
var feeBodyMarkers=feeRow.Markers.Where(m=>m.Part=="body").ToList();
var deletedFeeSentence="회원은 언제든지 서비스를 통하여 제공되는 상품과 용역의 가격을 수정할 수 있습니다.";
var addedFeeSentence="다만, 관련 법령 및 이 약관에 따른 청약철회·환급 등 회원의 권리는 제한되지 않습니다.";
Check(feeRow.Markers.Count(m=>m.Part=="header" && m.Message.Contains("26") &&
    m.Message.Contains("27"))==1,
    "article 26 -> 27 number lineage disappeared while correcting body diff");
Check(feeBodyMarkers.Count==2 &&
    feeBodyMarkers.Count(m=>m.Action=="삭제" && m.Text==deletedFeeSentence)==1 &&
    feeBodyMarkers.Count(m=>m.Action=="추가" && m.Text==addedFeeSentence)==1,
    "fee article must report only deleted price-change sentence and new refund sentence: "+
    string.Join(" | ",feeBodyMarkers.Select(m=>m.Message)));
var firstFeeSentence="회원은 회원 또는 회원에게 등록된 계정을 이용하는 누군가에 의하여 발생하는 모든 비용과 해당 세금을 지급하는 것에 동의합니다.";
var lastFeeSentence="회원은 계정이 차별적 또는 비차별적으로 폐쇄되었을 때, 미사용 가상 아이템에 관하여 현금을 수령하거나 또는 다른 배상을 받지 않는다는 점에 동의합니다.";
Check(feeOldBody.Contains(firstFeeSentence) && feeNewBody.Contains(firstFeeSentence) &&
    feeOldBody.Contains(lastFeeSentence) && feeNewBody.Contains(lastFeeSentence) &&
    feeBodyMarkers.All(m=>!m.Text.Contains(firstFeeSentence) && !m.Text.Contains(lastFeeSentence)),
    "unchanged substantial sentences must not be marked as deleted or inserted");
var bodyOffsetOld=feeRow.Members[0]!.Header.Length+1;
var bodyOffsetNew=feeRow.Members[1]!.Header.Length+1;
var deletedMarker=feeBodyMarkers.Single(m=>m.Action=="삭제");
var addedMarker=feeBodyMarkers.Single(m=>m.Action=="추가");
Check(deletedMarker.Endpoints.Any(ep=>ep.TargetDoc==0 &&
    ep.CharStart==bodyOffsetOld+feeOldBody.IndexOf(deletedFeeSentence,StringComparison.Ordinal) &&
    ep.CharEnd==ep.CharStart+deletedFeeSentence.Length) &&
    addedMarker.Endpoints.Any(ep=>ep.TargetDoc==1 &&
    ep.CharStart==bodyOffsetNew+feeNewBody.IndexOf(addedFeeSentence,StringComparison.Ordinal) &&
    ep.CharEnd==ep.CharStart+addedFeeSentence.Length),
    "fee article Word/UI spans must anchor precisely to the deleted/inserted sentences");
Check(!feeRow.DisplayMessages.Any(message=>message.Contains("변경: “"+firstFeeSentence)),
    "fee article change sidebar must not quote the entire unchanged paragraph");

// The same stable sentence in the middle must survive even if both
// surrounding sentences are changed. An unrelated long rewrite should
// still be eligible for the original whole-block treatment.
var sentenceGate=typeof(NativeComparisonEngine).GetMethod(
    "HasSubstantialSharedSentence",BindingFlags.NonPublic|BindingFlags.Static)!;
var preservedMiddle="이용자는 개인정보 처리의 목적과 이용 기간을 명확하게 확인할 수 있으며 필요한 사항을 요청할 수 있습니다.";
var middleOld="회사는 기존 계약 조건을 적용하고 분기별 자료를 공개합니다. "+
    preservedMiddle+" 기존 문의는 담당 부서로 전화하여 접수합니다.";
var middleNew="회사는 새로운 계약 기준과 별도의 안내 절차를 도입합니다. "+
    preservedMiddle+" 신규 문의는 온라인 접수 화면을 이용합니다.";
Check((bool)sentenceGate.Invoke(null,new object[]{middleOld,middleNew})!,
    "long unchanged middle sentence must remain a valid diff anchor");
var middleA=Path.Combine(dir,"middle-sentence-before.docx");
var middleB=Path.Combine(dir,"middle-sentence-after.docx");
Make(middleA,P("제31조(민감정보 처리)")+P(middleOld));
Make(middleB,P("제31조(민감정보 처리)")+P(middleNew));
var middleResult=await eng.CompareAsync(new[]{middleA,middleB},0,"legal",true,true);
var middleRow=middleResult.Rows.Single(r=>r.Members[0]?.Number=="31");
Check(middleRow.Markers.Any(m=>m.Part=="body") &&
    middleRow.Markers.Where(m=>m.Part=="body").All(m=>
        !m.Text.Contains(preservedMiddle,StringComparison.Ordinal)),
    "mid-paragraph common sentence must not be highlighted during two distinct rewrites");
Check(!(bool)sentenceGate.Invoke(null,new object[]{
    "기존 계약 조건을 적용합니다. 과거 자료는 회사에서 별도 보관합니다.",
    "신규 이용 기준을 적용합니다. 현재 자료는 서버에서 일괄 삭제합니다."})!,
    "unrelated or short boilerplate must not trigger a false sentence anchor");
var whitespaceAnchorA=firstFeeSentence+" "+lastFeeSentence;
var whitespaceAnchorB=firstFeeSentence.Replace("비용과 해당","비용과  해당")+" "+
    lastFeeSentence.Replace("계정이 차별적","계정이  차별적");
Check(!(bool)sentenceGate.Invoke(null,new object[]{whitespaceAnchorA,whitespaceAnchorB})!,
    "changed internal whitespace must not be mistaken for an unchanged sentence anchor");
Console.WriteLine("PASS V5.21.9 FEE ARTICLE WORD DIFF PRESERVED SENTENCES");

// 180. Whole NativeComparisonEngine.MatchParts pipeline, not individual
// candidate stages: verify every returned old/new index and score against
// original managed code for real legal structures and seeded edits.
if (NativeCodeVersionComparer.IsAvailable)
{
    var wholeType=typeof(NativeComparisonEngine);
    var wholeParser=wholeType.GetMethod("ParsePartsCSharp",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var wholeCSharp=wholeType.GetMethod("MatchPartsCSharp",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var wholeLive=wholeType.GetMethod("MatchParts",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var wholeMatched=wholeType.GetProperty("WholeMatchRustParityMatches",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var wholeMismatched=wholeType.GetProperty("WholeMatchRustParityMismatches",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    long MatchesWhole()=>(long)wholeMatched.GetValue(null)!;
    long MissesWhole()=>(long)wholeMismatched.GetValue(null)!;
    var initialWholeMatches=MatchesWhole();
    var initialWholeMismatches=MissesWhole();
    (int Old,int New,double Score)[] FetchWhole(
        System.Reflection.MethodInfo method,object oldParts,object newParts) =>
        ((System.Collections.IEnumerable)method.Invoke(null,new[]{oldParts,newParts})!)
        .Cast<object>().Select(m=>(
            Old:(int)m.GetType().GetProperty("Old")!.GetValue(m)!,
            New:(int)m.GetType().GetProperty("New")!.GetValue(m)!,
            Score:(double)m.GetType().GetProperty("Score")!.GetValue(m)!)).ToArray();
    var wholeDocs=new List<string> {
        "① 개인정보 수집 방법\n1. 이용자의 동의\n2. 개인정보 처리기간\n② 개인정보 보관기간",
        "① 개인정보 수집 방법\n1. 이용자의 동의\n2. 개인정보 처리기간",
        "① 개인정보 수집 방법\n1. 신규 개인정보 활용\n2. 이용자의 동의\n3. 개인정보 처리기간\n② 개인정보 보관기간",
        "① 신청인의 동의서를 제출하여야 한다.\n② 신청인이 기한 내에 이의를 제기할 수 있다.",
        "1. 신청인의 동의서를 제출하여야 한다.\n2. 신청인이 기한 내에 이의를 제기할 수 있다.",
        "① 원칙\n1. 첫 번째 항\n2. 두 번째 항",
        "(1) 첫 번째 항\n(2) 두 번째 항",
        "본문만 존재하는 문서의 원칙입니다.",
        "① 신청인의 동의서를 제출한다.\n② 이의 신청은 서면으로 한다.",
        "1. 신청인의 동의서를 제출한다.\n2. 이의 신청은 서면으로 한다.",
        "① 첫 번째\n② 두 번째\n③ 세 번째",
        "① 세 번째\n② 첫 번째\n③ 두 번째",
        "가. 신청인의 증빙자료 제출\n나. 증빙자료 확인 절차\n다. 대상자의 이의신청",
        "가. 신청인의 증빙자료 수정\n나. 증빙자료 확인 절차\n다. 대상자의 이의신청",
        "(i) 첫째\n(ii) 둘째\n(iii) 셋째",
        "① 예외처리\n가. 추가자료\n나. 반환절차",
        "",
    };
    var frozenWhole=System.Text.Json.JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory,"Fixtures","document_parts_golden.json")));
    wholeDocs.AddRange(frozenWhole.RootElement.EnumerateArray()
        .Select(x=>x.GetProperty("input").GetString()!));
    var seededWhole=new Random(20261023);
    var labelsWhole=new[]{"①","②","③","④","1.","2.","3.","(1)","(2)","가.","나.","(i)","(ii)"};
    var textsWhole=new[]{"개인정보 수집 목적을 안내합니다","개인정보를 보관합니다",
        "이용자의 동의를 요청합니다","법령이 정하는 기간 동안 보존합니다",
        "처리자 접근 권한을 제한합니다","이용자가 이의신청을 할 수 있습니다"};
    for(var k=0;k<120;k++)
    {
        var lines=Enumerable.Range(0,seededWhole.Next(1,10))
            .Select(_=>labelsWhole[seededWhole.Next(labelsWhole.Length)]+" "+
                textsWhole[seededWhole.Next(textsWhole.Length)]);
        wholeDocs.Add(string.Join("\n",lines));
    }
    // The Rust full matcher already accepts 64 parts. Confirm that the
    // production C# bridge does not truncate the source to the old 32 limit.
    // Reordered unique bodies must retain their original counterparts.
    var biggerWholeParts=string.Join("\n",Enumerable.Range(0,48)
        .Select(i=>$"({i+1}) Unique document clause {i:D3} details"));
    var biggerWholeReordered=string.Join("\n",Enumerable.Range(0,48)
        .Reverse().Select(i=>$"({i+1}) Unique document clause {i:D3} details"));
    wholeDocs.Add(biggerWholeParts);
    wholeDocs.Add(biggerWholeReordered);
    var checkedWhole=0;
    foreach(var originalText in wholeDocs)
    {
        var allTargets=new[]{originalText,wholeDocs[(checkedWhole*11+9)%wholeDocs.Count]};
        foreach(var alteredText in allTargets)
        {
            var oldPart=wholeParser.Invoke(null,new object[]{originalText})!;
            var newPart=wholeParser.Invoke(null,new object[]{alteredText})!;
            var oldMatches=FetchWhole(wholeCSharp,oldPart,newPart);
            var liveMatches=FetchWhole(wholeLive,oldPart,newPart);
            Check(oldMatches.Length==liveMatches.Length &&
                oldMatches.Zip(liveMatches).All(x=>x.First.Old==x.Second.Old &&
                    x.First.New==x.Second.New &&
                    Math.Abs(x.First.Score-x.Second.Score)<1e-12),
                "whole MatchParts Rust diverged from source matching: "+originalText+
                " -> "+alteredText);
            checkedWhole++;
        }
    }
    // 96-part exact reordering must be Rust-owned with no managed
    // candidate-feature matrix and exact old/new lineage.
    var manyExactOld=string.Join("\n",Enumerable.Range(1,96)
        .Select(i=>$"({i}) Unique source clause {i:D3}"));
    var manyExactNew=string.Join("\n",Enumerable.Range(1,96).Reverse()
        .Select(i=>$"({i}) Unique source clause {i:D3}"));
    var oldExactParts=wholeParser.Invoke(null,new object[]{manyExactOld})!;
    var newExactParts=wholeParser.Invoke(null,new object[]{manyExactNew})!;
    Check(((System.Collections.IEnumerable)oldExactParts).Cast<object>().Count()==96 &&
          ((System.Collections.IEnumerable)newExactParts).Cast<object>().Count()==96,
        "96-part exact ordering regression parser setup failed");
    var fastProp=wholeType.GetProperty("WholeExactRustMatches",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var fastBefore=(long)fastProp.GetValue(null)!;
    var exactReference=FetchWhole(wholeCSharp,oldExactParts,newExactParts);
    var exactProduction=FetchWhole(wholeLive,oldExactParts,newExactParts);
    Check(exactReference.Length==96 && exactProduction.Length==96 &&
        exactReference.Zip(exactProduction).All(x=>
            x.First.Old==x.Second.Old && x.First.New==x.Second.New &&
            Math.Abs(x.First.Score-x.Second.Score)<1e-12) &&
        (long)fastProp.GetValue(null)!>fastBefore,
        "Rust-only exact full matcher must preserve 96-part reorder lineage");
    checkedWhole++;
    // Ensure >256 uniquely reordered parts use the Rust-owned full-match
    // entrypoint rather than the former C# fallback. The oracle has to
    // agree on positions and scores, not only the overall pair count.
    var giantExactOld=string.Join("\n",Enumerable.Range(1,320)
        .Select(i=>$"({i}) Distinct exact contract provision {i:D4}"));
    var giantExactNew=string.Join("\n",Enumerable.Range(1,320).Reverse()
        .Select(i=>$"({i}) Distinct exact contract provision {i:D4}"));
    var giantOldParts=wholeParser.Invoke(null,new object[]{giantExactOld})!;
    var giantNewParts=wholeParser.Invoke(null,new object[]{giantExactNew})!;
    Check(((System.Collections.IEnumerable)giantOldParts).Cast<object>().Count()==320 &&
        ((System.Collections.IEnumerable)giantNewParts).Cast<object>().Count()==320,
        "320-part native exact reorder parser setup failed");
    var giantFastBefore=(long)fastProp.GetValue(null)!;
    var giantRustBefore=MatchesWhole();
    var giantCanonical=FetchWhole(wholeCSharp,giantOldParts,giantNewParts);
    var giantNative=FetchWhole(wholeLive,giantOldParts,giantNewParts);
    Check(giantCanonical.Length==320 && giantNative.Length==320 &&
        giantCanonical.Zip(giantNative).All(x=>
            x.First.Old==x.Second.Old && x.First.New==x.Second.New &&
            Math.Abs(x.First.Score-x.Second.Score)<1e-12) &&
        MatchesWhole()==giantRustBefore+1 &&
        ((long)fastProp.GetValue(null)!)==giantFastBefore+1,
        "320-part exact reordering must use Rust and match managed lineage");
    checkedWhole++;
    // Nonidentical documents beyond the legacy 64-part cap now use Rust
    // without exceeding the independent candidate-stage 8,192 pair budget.
    var bigChangedOld=string.Join("\n",Enumerable.Range(1,96)
        .Select(i=>$"({i}) Unique clause {i:D3} baseline content"));
    var bigChangedNew=string.Join("\n",Enumerable.Range(1,80)
        .Select(i=>$"({i}) Unique clause {i:D3} revised content"));
    var bigChangedOldParts=wholeParser.Invoke(null,new object[]{bigChangedOld})!;
    var bigChangedNewParts=wholeParser.Invoke(null,new object[]{bigChangedNew})!;
    Check(((System.Collections.IEnumerable)bigChangedOldParts).Cast<object>().Count()==96 &&
        ((System.Collections.IEnumerable)bigChangedNewParts).Cast<object>().Count()==80,
        "large nonidentical full matcher parsing setup failed");
    var beforeLarge=MatchesWhole();
    var bigExpected=FetchWhole(wholeCSharp,bigChangedOldParts,bigChangedNewParts);
    var bigActual=FetchWhole(wholeLive,bigChangedOldParts,bigChangedNewParts);
    Check(bigActual.Length==bigExpected.Length &&
        bigActual.Zip(bigExpected).All(x=>x.First.Old==x.Second.Old &&
            x.First.New==x.Second.New &&
            Math.Abs(x.First.Score-x.Second.Score)<1e-12) &&
        MatchesWhole()==beforeLarge+1,
        "96 x 80 nonidentical full parts must preserve native Rust parity");
    checkedWhole++;
    // Semantic full matching above the old 8,192-pair budget must stay
    // in native Rust and match the independent C# matching oracle.
    var biggerSemanticOld=string.Join("\n",Enumerable.Range(1,144)
        .Select(i=>$"({i}) Document provision {i:D3} baseline wording"));
    var biggerSemanticNew=string.Join("\n",Enumerable.Range(1,96)
        .Select(i=>$"({i}) Document provision {i:D3} revised wording"));
    var biggerSemanticOldParts=wholeParser.Invoke(null,new object[]{biggerSemanticOld})!;
    var biggerSemanticNewParts=wholeParser.Invoke(null,new object[]{biggerSemanticNew})!;
    Check(((System.Collections.IEnumerable)biggerSemanticOldParts).Cast<object>().Count()==144 &&
        ((System.Collections.IEnumerable)biggerSemanticNewParts).Cast<object>().Count()==96,
        "144 x 96 semantic Rust matcher parser setup failed");
    var biggerSemanticBefore=MatchesWhole();
    var biggerSemanticExpected=FetchWhole(wholeCSharp,biggerSemanticOldParts,biggerSemanticNewParts);
    var biggerSemanticActual=FetchWhole(wholeLive,biggerSemanticOldParts,biggerSemanticNewParts);
    Check(biggerSemanticActual.Length==biggerSemanticExpected.Length &&
        biggerSemanticActual.Zip(biggerSemanticExpected).All(x=>
            x.First.Old==x.Second.Old && x.First.New==x.Second.New &&
            Math.Abs(x.First.Score-x.Second.Score)<1e-12) &&
        MatchesWhole()==biggerSemanticBefore+1,
        "144 x 96 semantic part matching must use Rust with C# score parity");
    checkedWhole++;
    Check(MissesWhole()==initialWholeMismatches,
        "whole MatchParts Rust differs from C# in tested document structures; "+
        (MissesWhole()-initialWholeMismatches)+" mismatches");
    Check(MatchesWhole()-initialWholeMatches==checkedWhole,
        "whole MatchParts must actually use native Rust for every bounded corpus pair: "+
        (MatchesWhole()-initialWholeMatches)+"/"+checkedWhole);
    Console.WriteLine($"PASS RUST WHOLE MATCHPARTS CSHARP PARITY {checkedWhole}/{checkedWhole}");
}

// 181. Entire generic document block matching: Rust and the original
// C# rank/greedy decision must agree even on ties and one-sided input.
if (NativeCodeVersionComparer.IsAvailable)
{
    var genericRandom=new Random(20261023);
    var cases=0;
    var scale=new[]{.0,.32,.4799,.48,.50,.60,.72,.90,.99,1.0};
    for(var round=0;round<500;round++)
    {
        var n=genericRandom.Next(0,18);
        var m=genericRandom.Next(0,18);
        var scores=Enumerable.Range(0,n*m).Select(_=>scale[
            genericRandom.Next(scale.Length)]).ToArray();
        var original = Enumerable.Range(0,n)
            .SelectMany(i=>Enumerable.Range(0,m).Select(j=>(
                Base:i,Other:j,Score:scores[i*m+j],
                Rank:scores[i*m+j]+(1.0-
                    Math.Min(Math.Abs(i-j)/(double)Math.Max(1,Math.Max(n,m)),1.0))*.015)))
            .Where(x=>x.Score>=.48)
            .OrderByDescending(x=>x.Rank).ToList();
        var usedOld=new HashSet<int>();var usedNew=new HashSet<int>();
        var expected=new List<(int Base,int Other,double Score)>();
        foreach(var genericEntry in original)
            if(usedOld.Add(genericEntry.Base)&&usedNew.Add(genericEntry.Other))
                expected.Add((genericEntry.Base,genericEntry.Other,genericEntry.Score));
        expected=expected.OrderBy(x=>x.Base).ToList();
        var rust=NativeCodeVersionComparer.MatchGenericDocumentUnits(n,m,scores);
        Check(rust is not null && rust.Count==expected.Count &&
            expected.Zip(rust).All(x=>
                x.First.Base==x.Second.Base&&x.First.Other==x.Second.Other &&
                Math.Abs(x.First.Score-x.Second.Score)<1e-12&&x.Second.Mode=="same"),
            "full generic Rust pair rank deviated from C#: "+round);
        cases++;
    }
    Check(NativeCodeVersionComparer.MatchGenericDocumentUnits(
        257,1,Enumerable.Repeat(.95,257).ToArray()) is null,
        "unsupported large generic matcher must safely fall back to C#");
    Console.WriteLine($"PASS RUST GENERIC BLOCK CSHARP PARITY {cases}/{cases}");
}

// Whole article lineage and non-article block ordering: reference C#
// against production Rust-first and check exact action/score/index parity.
if (NativeCodeVersionComparer.IsAvailable)
{
    var t=typeof(NativeComparisonEngine);
    var parser=t.GetMethod("ParseUnits",BindingFlags.NonPublic|BindingFlags.Static)!;
    var original=t.GetMethod("MatchUnitSequenceCSharp",BindingFlags.NonPublic|BindingFlags.Static)!;
    var active=t.GetMethod("MatchUnitSequence",BindingFlags.NonPublic|BindingFlags.Static)!;
    var accepts=t.GetProperty("UnitLineageRustMatches",BindingFlags.NonPublic|BindingFlags.Static)!;
    var rejects=t.GetProperty("UnitLineageRustMismatches",BindingFlags.NonPublic|BindingFlags.Static)!;
    long Counter(System.Reflection.PropertyInfo property)=>(long)property.GetValue(null)!;
    var acceptedBefore=Counter(accepts);
    var rejectedBefore=Counter(rejects);
    (int Base,int Other,double Score,string Mode)[] Invoke(
        System.Reflection.MethodInfo method,object left,object right)=>
        ((System.Collections.IEnumerable)method.Invoke(null,new[]{
            left,right,(object)CancellationToken.None})!).Cast<object>()
            .Select(x=>(Base:(int)x.GetType().GetProperty("Base")!.GetValue(x)!,
                Other:(int)x.GetType().GetProperty("Other")!.GetValue(x)!,
                Score:(double)x.GetType().GetProperty("Score")!.GetValue(x)!,
                Mode:(string)x.GetType().GetProperty("Mode")!.GetValue(x)!)).ToArray();
    var oldTexts=new[]{
        "제1조(목적)\n이 약관은 서비스 이용과 개인정보 처리 기준을 정한다.\n제2조(개인정보 보호)\n이용자에게 처리방침을 고지한다.\n제3조(지급)\n정해진 수수료를 납부한다.",
        "제1조(지급)\n정해진 수수료를 납부한다.\n제2조(목적)\n이 약관은 서비스 이용과 개인정보 처리 기준을 정한다.\n제3조(개인정보 보호)\n이용자에게 처리방침을 고지한다.",
        "Article 1 (Purpose)\nThe service terms regulate the usage of member accounts.\nArticle 2 (Privacy)\nMembers receive information about data handling.\nArticle 3 (Payment)\nMembers pay the necessary charges.",
        "제1조(총칙)\n본 약관은 회사와 회원 간의 서비스 이용 계약을 규율한다.\n제2조(총칙)\n회사는 회원의 권리를 보호한다.",
        "제1조(개인정보)\n회원의 개인정보는 적법하게 처리됩니다.\n제2조(지급)\n회원은 모든 이용요금을 납부합니다.",
        "전문\n이 문서는 개인정보 보호 의무에 대한 공통 기준을 정의한다.\n제1조(목적)\n개인정보를 보호한다.\n제2조(권리)\n회원의 권리를 보장한다.",
    };
    var randomLineage=new Random(20261023);
    var titles=new[]{"목적","개인정보 보호","개인정보","지급","정의","권리","서비스 제공","총칙","보관","해지","변경"};
    var bodies=new[]{"이용자의 동의를 받아야 한다.","회원에게 필요한 정보를 고지한다.",
        "이용약관에 정한 목적을 위하여 처리한다.","관련 법령에 따라 환급 절차를 안내한다.",
        "보유기간이 끝난 자료는 삭제한다.","회원은 서비스의 이용요금을 납부한다."};
    var corpus=new List<string>(oldTexts);
    for(var c=0;c<54;c++)
    {
        var units=Enumerable.Range(0,randomLineage.Next(2,8))
            .Select(i=>$"제{i+1}조({titles[randomLineage.Next(titles.Length)]})\n"+
                bodies[randomLineage.Next(bodies.Length)]);
        corpus.Add(string.Join("\n",units));
    }
    var count=0;
    foreach(var doc in corpus)
    {
        var list= new[] {doc,corpus[(count*13+7)%corpus.Count]};
        foreach(var next in list)
        {
            var left=parser.Invoke(null,new object[]{doc,"legal"})!;
            var right=parser.Invoke(null,new object[]{next,"legal"})!;
            var csharp=Invoke(original,left,right);
            var rust=Invoke(active,left,right);
            Check(csharp.Length==rust.Length &&
                csharp.Zip(rust).All(x=>
                    x.First.Base==x.Second.Base && x.First.Other==x.Second.Other &&
                    x.First.Mode==x.Second.Mode &&
                    Math.Abs(x.First.Score-x.Second.Score)<1e-12),
                "Rust article lineage mismatch on document pair "+count+
                " source="+doc+" target="+next);
            count++;
        }
    }
    Check(Counter(rejects)==rejectedBefore,
        "article lineage parity gate rejected "+(Counter(rejects)-rejectedBefore)+
        " native outputs despite the regression corpus passing by C# fallback");
    Check(Counter(accepts)-acceptedBefore==count,
        "whole article lineage path did not use Rust for all supported cases "+
        (Counter(accepts)-acceptedBefore)+"/"+count);
    Console.WriteLine($"PASS RUST WHOLE ARTICLE LINEAGE CSHARP PARITY {count}/{count}");
}

// Canonical DOCX table-cell old/new pairing: regression Golden and
// verification counters must show the *whole* current table test corpus
// uses Rust without silently dropping back to managed correspondence.
if (NativeCodeVersionComparer.IsAvailable)
{
    var tablePairs=NativeCodeVersionComparer.MatchDocumentTableCells(
        new[]{
            new NativeCodeVersionComparer.DocumentTableCell(0,0,0,"name"),
            new NativeCodeVersionComparer.DocumentTableCell(1,0,0,"value"),
            new NativeCodeVersionComparer.DocumentTableCell(2,0,1,"a"),
            new NativeCodeVersionComparer.DocumentTableCell(3,0,1,"1"),
            new NativeCodeVersionComparer.DocumentTableCell(4,0,2,"b"),
            new NativeCodeVersionComparer.DocumentTableCell(5,0,2,"2")
        },
        new[]{
            new NativeCodeVersionComparer.DocumentTableCell(0,0,0,"name"),
            new NativeCodeVersionComparer.DocumentTableCell(1,0,0,"value"),
            new NativeCodeVersionComparer.DocumentTableCell(2,0,1,"b"),
            new NativeCodeVersionComparer.DocumentTableCell(3,0,1,"2"),
            new NativeCodeVersionComparer.DocumentTableCell(4,0,2,"a"),
            new NativeCodeVersionComparer.DocumentTableCell(5,0,2,"1")
        });
    Check(tablePairs is not null &&
        tablePairs.Select(x=>(x.Base,x.Other))
            .SequenceEqual(new[]{(0,0),(1,1),(2,4),(3,5),(4,2),(5,3)}),
        "Rust Word table rows must retain row anchors after reorder");
    var emptyUnmatched=NativeCodeVersionComparer.MatchDocumentTableCells(
        new[]{
            new NativeCodeVersionComparer.DocumentTableCell(0,0,0,"x"),
            new NativeCodeVersionComparer.DocumentTableCell(1,0,0,"x")
        },
        new[]{
            new NativeCodeVersionComparer.DocumentTableCell(0,0,0,"x"),
            new NativeCodeVersionComparer.DocumentTableCell(1,0,0,"new"),
            new NativeCodeVersionComparer.DocumentTableCell(2,0,0,"x")
        });
    Check(emptyUnmatched is not null && emptyUnmatched.Count==0,
        "ambiguous duplicate table cells must not bridge an inserted column");
    var tableType=typeof(NativeComparisonEngine);
    var tableMatches=(long)tableType.GetProperty("TableCellRustMatches",
        BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
    var tableMismatches=(long)tableType.GetProperty("TableCellRustMismatches",
        BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
    Check(tableMatches>=15 && tableMismatches==0,
        "Word DOCX table corpus must actually execute Rust with zero C# parity mismatches: "+
        tableMatches+" accepted, "+tableMismatches+" rejected");
    Console.WriteLine($"PASS RUST FULL DOCX TABLE CORRESPONDENCE {tableMatches} actual comparisons; 0 drift");
}

// Entire token-anchor longest-block matcher against frozen C# reference.
if (NativeCodeVersionComparer.IsAvailable)
{
    var method=typeof(NativeComparisonEngine).GetMethod("MatcherBlocksCSharp",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var live=typeof(NativeComparisonEngine).GetMethod("MatcherBlocks",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var accepts=typeof(NativeComparisonEngine).GetProperty("TokenMatchRustMatches",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var rejects=typeof(NativeComparisonEngine).GetProperty("TokenMatchRustMismatches",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    long Count(System.Reflection.PropertyInfo p)=>(long)p.GetValue(null)!;
    var start=Count(accepts);var mismatchStart=Count(rejects);
    var tokens=new[]{"a","a","b","the","and","privacy","개인정보","보호","동의",
        "없이","Article","1","2","3","x","y","z","of","③","④"};
    var rng=new Random(20261023);
    var checkedCases=0;
    (int A,int B,int Size)[] CSharpBlockPairs(System.Reflection.MethodInfo fn,
        string[] x,string[] y)=>
        ((System.Collections.IEnumerable)fn.Invoke(null,new object[]{x,y})!)
        .Cast<object>().Select(block=>{
            var type=block.GetType();
            return ((int)type.GetField("Item1")!.GetValue(block)!,
                (int)type.GetField("Item2")!.GetValue(block)!,
                (int)type.GetField("Item3")!.GetValue(block)!);
        }).ToArray();
    for(var n=0;n<700;n++)
    {
        var before=Enumerable.Range(0,rng.Next(0,38))
            .Select(_=>tokens[rng.Next(tokens.Length)]).ToArray();
        var after=Enumerable.Range(0,rng.Next(0,38))
            .Select(_=>tokens[rng.Next(tokens.Length)]).ToArray();
        var canonical=CSharpBlockPairs(method,before,after);
        var rust=NativeCodeVersionComparer.MatchDocumentTokenBlocks(before,after);
        Check(rust is not null && canonical.Length==rust.Count &&
            canonical.Zip(rust).All(pair=>
                pair.First.A==pair.Second.A &&
                pair.First.B==pair.Second.B &&
                pair.First.Size==pair.Second.Size),
            "Rust token longest-block matching differs from C# baseline: "+n);
        var direct=CSharpBlockPairs(live,before,after);
        Check(direct.SequenceEqual(canonical),
            "live Rust token matcher changed canonical word-review span: "+n);
        checkedCases++;
    }
    Check(Count(accepts)-start==checkedCases && Count(rejects)==mismatchStart,
        "Rust token matcher must be adopted for all bounded test pairs, zero mismatch");
    Console.WriteLine($"PASS RUST TOKEN LONGEST BLOCK CSHARP PARITY {checkedCases}/{checkedCases}");
}

// Whole Rust-first UTF-16 character similarity parity, not only string
// token alignment: long legal titles, Korean, surrogate pairs, and repeats.
if (NativeCodeVersionComparer.IsAvailable)
{
    var owner=typeof(NativeComparisonEngine);
    var scoreIndel=owner.GetMethod("IndelRatioCSharp",BindingFlags.NonPublic|BindingFlags.Static)!;
    var seq=owner.GetMethod("SequenceRatioCSharp",BindingFlags.NonPublic|BindingFlags.Static)!;
    var nativeIndel=owner.GetMethod("IndelRatio",BindingFlags.NonPublic|BindingFlags.Static)!;
    var nativeSeq=owner.GetMethod("SequenceRatio",BindingFlags.NonPublic|BindingFlags.Static)!;
    var accepts=owner.GetProperty("CharacterScoreRustMatches",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var rejects=owner.GetProperty("CharacterScoreRustMismatches",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    long Counter(System.Reflection.PropertyInfo property)=>
        (long)property.GetValue(null)!;
    double InvokeScore(System.Reflection.MethodInfo method,string a,string b)=>
        (double)method.Invoke(null,new object[]{a,b})!;
    var acceptedStart=Counter(accepts);var rejectedStart=Counter(rejects);
    var rng=new Random(20261023);
    var terms=new[]{"a","b","A","한","글","동","의","개","인","보","호"," ",
        "①","(1)","법","령","의","미","😀","😃","™","é","e\u0301","\n","X"};
    var checkedScores=0;
    for(var i=0;i<420;i++)
    {
        var lenA=i<24?i%6:rng.Next(0,65);
        var lenB=i<24?(i+3)%6:rng.Next(0,65);
        var a=string.Concat(Enumerable.Range(0,lenA)
            .Select(_=>terms[rng.Next(terms.Length)]));
        var b=i%4==0?a:string.Concat(Enumerable.Range(0,lenB)
            .Select(_=>terms[rng.Next(terms.Length)]));
        foreach(var (mode,method,live) in new[]{
            ("indel",scoreIndel,nativeIndel),("sequence",seq,nativeSeq)})
        {
            var expected=InvokeScore(method,a,b);
            var rust=NativeCodeVersionComparer.ScoreDocumentCharacters(a,b,mode);
            Check(rust is not null && Math.Abs(expected-rust.Value)<1e-12,
                "Rust native character score disagrees with original C#: "+i+" "+mode);
            var liveScore=InvokeScore(live,a,b);
            Check(Math.Abs(expected-liveScore)<1e-12,
                "Rust-first character scoring modified the legal matcher: "+i+" "+mode);
            checkedScores++;
        }
    }
    Check(Counter(rejects)==rejectedStart &&
        Counter(accepts)-acceptedStart==checkedScores,
        "UTF-16 scoring was not executed on every test with zero parity drift");
    Console.WriteLine($"PASS RUST UTF16 DOCUMENT CHARACTER SCORE PARITY {checkedScores}/{checkedScores}");
}

// Rust-first *token similarity score* across real Korean/English review
// tokens; this is a separate contract from longest matching-block indices.
if (NativeCodeVersionComparer.IsAvailable)
{
    var rng=new Random(20261023);
    var vocab=new[]{"계약","회원","개인정보","수집","동의","없이","환급",
        "Article","Privacy","the","of","pay","and","😀","A","B","⓵","1"};
    var beforeCount=(long)typeof(NativeComparisonEngine).GetProperty(
        "TokenScoreRustMatches",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
    var beforeMismatch=(long)typeof(NativeComparisonEngine).GetProperty(
        "TokenScoreRustMismatches",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
    var checkedValues=0;var adoptedValues=0;
    for(var k=0;k<480;k++)
    {
        var a=string.Join(" ",Enumerable.Range(0,rng.Next(1,12))
            .Select(_=>vocab[rng.Next(vocab.Length)]));
        var b=k%3==0?a:string.Join(" ",Enumerable.Range(0,rng.Next(1,12))
            .Select(_=>vocab[rng.Next(vocab.Length)]));
        var original=(double)typeof(NativeComparisonEngine).GetMethod("SimilarityCSharp",
            BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{a,b})!;
        var production=(double)typeof(NativeComparisonEngine).GetMethod("Similarity",
            BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{a,b})!;
        Check(Math.Abs(original-production)<1e-12,
            "Rust token similarity differs from canonical C# LCS: "+k);
        if (a!=b && a.Length<=512 && b.Length<=512) adoptedValues++;
        checkedValues++;
    }
    var adoptedAfter=(long)typeof(NativeComparisonEngine).GetProperty(
        "TokenScoreRustMatches",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
    var mismatchAfter=(long)typeof(NativeComparisonEngine).GetProperty(
        "TokenScoreRustMismatches",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
    Check(mismatchAfter==beforeMismatch &&
          adoptedAfter-beforeCount==adoptedValues,
        "Rust token scoring did not execute consistently with zero parity fallbacks");
    Console.WriteLine($"PASS RUST TOKEN LCS SIMILARITY PARITY {checkedValues}/{checkedValues}");
}

// Korean short-word morphological alignment must execute the new Rust
// dynamic-programming selector, without any change to marker positions.
if (NativeCodeVersionComparer.IsAvailable)
{
    var engineType=typeof(NativeComparisonEngine);
    var wrapper=engineType.GetMethod("TryKoreanReviewDiff",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var original=engineType.GetMethod("TryKoreanReviewDiffCSharp",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var accepted=engineType.GetProperty("KoreanReviewRustMatches",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var rejected=engineType.GetProperty("KoreanReviewRustMismatches",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    long ReadKoreanCounter(System.Reflection.PropertyInfo prop)=>(long)prop.GetValue(null)!;
    var initialAccepted=ReadKoreanCounter(accepted);
    var initialRejected=ReadKoreanCounter(rejected);
    var corpus=new[]{
        ("개인정보 동의서 제출기한 요구","개인정보 동의서는 제출기한을 요구"),
        ("개인정보 동의서 제출기한 요청","개인정보 동의서를 제출기한에 요청"),
        ("개인정보 동의서 제출기한 확인","개인정보 동의서를 제출기한이 확인"),
        ("개인정보 동의서 제출기한 관리","개인정보 동의서는 제출기한이 관리"),
    };
    var koreanChecked=0;
    foreach(var (a,b) in corpus)
    {
        object?[] koreanReviewArgs={a,b,0,0,0,1,"A↔B",0,"body",0};
        var source=(System.Collections.IEnumerable?)original.Invoke(null,koreanReviewArgs);
        var actual=(System.Collections.IEnumerable?)wrapper.Invoke(null,koreanReviewArgs);
        Check((source is null)==(actual is null),
            "Rust Korean review eligibility differs from C#");
        var expected=source?.Cast<object>().ToArray() ?? Array.Empty<object>();
        var produced=actual?.Cast<object>().ToArray() ?? Array.Empty<object>();
        Check(expected.Length==produced.Length &&
            expected.Zip(produced).All(pair=>
            {
                var type=pair.First.GetType();
                return type.GetProperty("Action")!.GetValue(pair.First)!.Equals(
                        type.GetProperty("Action")!.GetValue(pair.Second)) &&
                    type.GetProperty("Text")!.GetValue(pair.First)!.Equals(
                        type.GetProperty("Text")!.GetValue(pair.Second)) &&
                    type.GetProperty("CharStart")!.GetValue(pair.First)!.Equals(
                        type.GetProperty("CharStart")!.GetValue(pair.Second)) &&
                    type.GetProperty("CharEnd")!.GetValue(pair.First)!.Equals(
                        type.GetProperty("CharEnd")!.GetValue(pair.Second));
            }),
            "Rust Korean DP changed visible Word review marker for "+a+" -> "+b);
        koreanChecked++;
    }
    Check(ReadKoreanCounter(rejected)==initialRejected &&
        ReadKoreanCounter(accepted)>initialAccepted,
        "Korean short-word regression must exercise Rust native DP");
    Console.WriteLine($"PASS RUST KOREAN MORPH DP CSHARP PARITY {koreanChecked}/{koreanChecked}");
}

// C# vs Rust review shading boundary/overlap/UTF-16 index parity.
// The live document regression additionally verifies BuildSegments uses
// Rust without silently falling back for the supported inputs.
if (NativeCodeVersionComparer.IsAvailable)
{
    var rng=new Random(20261024);
    var total=0;
    for(var k=0;k<550;k++)
    {
        var text=k%4==0?"주문을 취소할 수 있습니다.":new string(
            Enumerable.Range(0,rng.Next(1,91)).Select(_=>(char)('a'+rng.Next(26))).ToArray());
        var count=rng.Next(0,12);
        var spans=new List<NativeCodeVersionComparer.DocumentReviewSpan>();
        var flags=new int[text.Length];
        for(var m=0;m<count;m++)
        {
            var first=rng.Next(0,text.Length+6);
            var last=first+rng.Next(0,Math.Max(1,text.Length+6-first));
            var role=rng.Next(2)==0?1:2;
            spans.Add(new(first,last,role));
            for(var i=Math.Min(first,text.Length);i<Math.Min(last,text.Length);i++)
                flags[i]|=role;
        }
        var expected=new List<(string Text,string Style)>();
        var offset=0;
        while(offset<text.Length)
        {
            var style=flags[offset];var end=offset+1;
            while(end<text.Length && flags[end]==style) end++;
            expected.Add((text[offset..end],style switch {
                1=>"delete",2=>"insert",3=>"both",_=>"normal"}));
            offset=end;
        }
        var native=NativeCodeVersionComparer.SegmentDocumentReview(text,spans);
        Check(native is not null && native.Count==expected.Count &&
            native.Zip(expected).All(x=>
                x.First.Text==x.Second.Text && x.First.Style==x.Second.Style),
            "Rust review text segmented incorrectly: "+k);
        total++;
    }
    var unicodeSplit=NativeCodeVersionComparer.SegmentDocumentReview(
        "a😀b",new[]{new NativeCodeVersionComparer.DocumentReviewSpan(1,2,1)});
    Check(unicodeSplit is null,
        "Rust must fall back if marker splits an astral UTF-16 surrogate pair");
    var counter=(long)typeof(NativeComparisonEngine).GetProperty(
        "ReviewSegmentsRustMatches",
        BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
    var rejected=(long)typeof(NativeComparisonEngine).GetProperty(
        "ReviewSegmentsRustMismatches",
        BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
    Check(counter>0 && rejected==0,
        "document regression did not use Rust review segmentation or found mismatch");
    Console.WriteLine($"PASS RUST DOCUMENT REVIEW SHADING SEGMENTS PARITY {total}/{total}");
}

// A/B/C row placement: 540 deterministic source-compatible matching
// matrices with two- and three-document inputs, moved indices and
// coalescing across inserted rows.
if (NativeCodeVersionComparer.IsAvailable)
{
    var rng=new Random(20261024);
    var cases=0;
    for(var trial=0;trial<540;trial++)
    {
        var docs=trial%2==0?2:3;
        var baseline=rng.Next(0,22);
        var counts=Enumerable.Range(0,docs).Select(d=>
            d==0?baseline:rng.Next(0,24)).ToArray();
        var mappings=new List<NativeCodeVersionComparer.DocumentRowMatch>();
        var newItems=new List<NativeCodeVersionComparer.DocumentRowAddition>();
        for(var d=1;d<docs;d++)
        {
            var baseOrder=Enumerable.Range(0,baseline).OrderBy(_=>rng.Next())
                .Take(Math.Min(baseline,counts[d])/2).ToArray();
            var otherOrder=Enumerable.Range(0,counts[d]).OrderBy(_=>rng.Next())
                .Take(baseOrder.Length).ToArray();
            var matched=baseOrder.Zip(otherOrder)
                .Select(p=>new NativeCodeVersionComparer.DocumentRowMatch(
                    d,p.First,p.Second)).ToArray();
            mappings.AddRange(matched);
            var used=otherOrder.ToHashSet();
            for(var other=0;other<counts[d];other++)
            {
                if(used.Contains(other))continue;
                var prev=matched.Where(x=>x.Other<other).OrderByDescending(x=>x.Other)
                    .FirstOrDefault();
                var next=matched.Where(x=>x.Other>other).OrderBy(x=>x.Other)
                    .FirstOrDefault();
                var slot=prev is not null && next is not null && prev.Base<next.Base
                    ? next.Base : prev is not null
                    ? Math.Min(baseline,prev.Base+1)
                    : next is not null ? Math.Max(0,next.Base) : baseline;
                newItems.Add(new(d,other,slot,other));
            }
        }
        var rowGrouped=newItems.GroupBy(x=>x.Slot)
            .OrderByDescending(x=>x.Key);
        var mergeable=new List<NativeCodeVersionComparer.DocumentRowMerge>();
        foreach(var group in rowGrouped)
        {
            var sorted=group.OrderBy(x=>x.UnitIndex).ThenBy(x=>x.Doc).ToArray();
            for(var i=0;i<sorted.Length;i++)
            for(var j=i+1;j<sorted.Length;j++)
                if(sorted[i].Doc!=sorted[j].Doc&&rng.Next(2)==0)
                    mergeable.Add(new(sorted[i].Doc,sorted[i].Other,
                        sorted[j].Doc,sorted[j].Other));
        }
        var expected=Enumerable.Range(0,baseline)
            .Select(i=>{
                var row=new int?[docs];row[0]=i;return row;
            }).ToList();
        foreach(var m in mappings) expected[m.Base][m.Doc]=m.Other;
        foreach(var group in rowGrouped)
        {
            var pending=new List<int?[]>();
            foreach(var a in group.OrderBy(x=>x.UnitIndex).ThenBy(x=>x.Doc))
            {
                var row=pending.FirstOrDefault(r=>{
                    if(r[a.Doc].HasValue)return false;
                    var existing=Enumerable.Range(0,docs)
                        .Where(d=>r[d].HasValue).Select(d=>(Doc:d,Index:r[d]!.Value))
                        .FirstOrDefault();
                    return mergeable.Any(m=>m.LeftDoc==existing.Doc &&
                        m.LeftOther==existing.Index &&
                        m.RightDoc==a.Doc && m.RightOther==a.Other);
                });
                if(row is null){row=new int?[docs];pending.Add(row);}
                row[a.Doc]=a.Other;
            }
            expected.InsertRange(Math.Clamp(group.Key,0,expected.Count),pending);
        }
        var native=NativeCodeVersionComparer.AlignDocumentRows(
            counts,0,mappings,newItems,mergeable);
        Check(native is not null && native.Count==expected.Count &&
            expected.Zip(native).All(pair=>pair.First.SequenceEqual(pair.Second)),
            "Rust A/B/C row ordering differs from canonical grouping: "+trial);
        cases++;
    }
    var rowCounter=(long)typeof(NativeComparisonEngine).GetProperty(
        "RowAlignmentRustMatches",
        BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
    var rowMismatch=(long)typeof(NativeComparisonEngine).GetProperty(
        "RowAlignmentRustMismatches",
        BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
    Check(rowCounter>0 && rowMismatch==0,
        "live document compare must actually adopt Rust A/B/C rows without drift");
    Console.WriteLine($"PASS RUST A/B/C ROW ALIGNMENT CSHARP PARITY {cases}/{cases}");
}

// Legal/general structural unit parsing parity. Inspect both complete
// arrays, not only the number of recognized article headings.
if (NativeCodeVersionComparer.IsAvailable)
{
    var engine=typeof(NativeComparisonEngine);
    var sourceMethod=engine.GetMethod("ParseUnitsCSharp",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var nativeMethod=engine.GetMethod("ParseUnits",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var accepted=engine.GetProperty("UnitParserRustMatches",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var mismatched=engine.GetProperty("UnitParserRustMismatches",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    long Counter(System.Reflection.PropertyInfo p)=>(long)p.GetValue(null)!;
    var acceptedStart=Counter(accepted);
    var mismatchStart=Counter(mismatched);
    var samples=new List<(string Text,string Mode)> {
        ("제1조(목적)\n회사는 회원의 권리를 보장한다.\n제2조(보관)\n정보를 보관한다.","legal"),
        ("Chapter 1. General\nArticle 1 (Purpose)\nBasic terms.\nArticle 2 (Rights)\nMember rights.","legal"),
        ("제1장 총칙\n제1절 계약\n제1조(목적)\n기본 원칙\n제2조(개인정보)\n보호조항","legal"),
        ("Preamble text\nArticle 1 (Introduction)\nPrivacy shall be ensured.\nArticle 2 (Terms)\nCompany must comply.","legal"),
        ("TITLE\nThe introductory text.\nPRIVACY\nPersonal information rights.","general"),
        ("1. Overview\nGeneral requirements.\n2. Background\nAdditional requirements.","general"),
        ("Intro paragraph.\nSecond paragraph.\nThird paragraph.","general"),
        ("Article IX (Use)\nOne.\nArticle X (Rights)\nTwo.","legal"),
        ("제3조의2(개정)\n추가 사항이 포함됩니다.","legal"),
        ("제1조(총칙) 내용\n① 개인정보 이용\n② 데이터 보호","legal"),
        ("","legal"),("","general")
    };
    var rng=new Random(20261024);
    var titles=new[]{"목적","의무","개인정보","계약","정의","권리","서비스","보관","지급","총칙"};
    var lines=new[]{"회원은 필요한 내용을 확인합니다","회사는 개인정보를 적법하게 처리합니다",
        "법률은 서비스의 범위를 정합니다","계약의 기본 조건을 명시합니다"};
    for(var k=0;k<135;k++)
    {
        var parts=Enumerable.Range(1,rng.Next(1,9)).Select(i=>
            $"제{i}조({titles[rng.Next(titles.Length)]})\n"+
            lines[rng.Next(lines.Length)]);
        samples.Add((string.Join("\n",parts),"legal"));
    }
    for(var k=0;k<135;k++)
    {
        var parts=Enumerable.Range(1,rng.Next(1,9)).Select(i=>
            i%2==0?$"SECTION {i}\nBasic requirements for {i}.":
                $"General paragraph number {i} shall be tested.");
        samples.Add((string.Join("\n",parts),"general"));
    }
    var parserCases=0; var parserDivergence=0;
    foreach(var item in samples)
    {
        var expected=((System.Collections.IEnumerable)sourceMethod.Invoke(null,
            new object[]{item.Text,item.Mode})!).Cast<object>().ToArray();
        var actual=((System.Collections.IEnumerable)nativeMethod.Invoke(null,
            new object[]{item.Text,item.Mode})!).Cast<object>().ToArray();
        string Key(object u)
        {
            var type=u.GetType();
            return string.Join("|",new[]{"Index","Kind","Number","Title","Header","Body","Section"}
                .Select(name=>type.GetProperty(name)!.GetValue(u)?.ToString()));
        }
        var different=expected.Length!=actual.Length ||
            !expected.Select(Key).SequenceEqual(actual.Select(Key));
        if(different)
        {
            parserDivergence++;
            if(parserDivergence<=5)
                Console.WriteLine("PARSER_DIFF "+item.Mode+" "+item.Text.Replace("\n"," ¶ ")+
                    " REF="+string.Join(";",expected.Select(Key))+
                    " LIVE="+string.Join(";",actual.Select(Key)));
        }
        parserCases++;
    }
    Check(parserDivergence==0 &&
        Counter(mismatched)==mismatchStart &&
        Counter(accepted)-acceptedStart==parserCases,
        "Rust document parser must adopt every bounded legal/general unit case");
    Console.WriteLine($"PASS RUST LEGAL/GENERAL UNIT PARSER PARITY {parserCases}/{parserCases}");
}

// Unicode normalization is part of the legal change/no-change contract.
// Verify the exact FormC/FormKC, invisible character and article prefix
// outputs for all three normalizer flavors before treating Rust as primary.
if (NativeCodeVersionComparer.IsAvailable)
{
    var engineType=typeof(NativeComparisonEngine);
    var methods=new[]{
        (Mode:"normalize",Source:"NormalizeCSharp"),
        (Mode:"lineage",Source:"LineageNormalizeCSharp"),
        (Mode:"visible",Source:"CanonicalVisibleTextCSharp")
    };
    var rng=new Random(20261024);
    var tokens=new[]{"AAA","ＡＢＣ","a\u0301","é","가","가","①",
        "Article 4","제3조","동의","없이","\u200b","\u200d","\ufeff",
        "\ufe0f"," ","\t","\n","\u00a0","(",")",",",":","9","–","!",
        "İ","ı","Ä","Δ","ß","Ł","😀","😃"};
    var corpus=new List<string>{
        "동의없이","동의 없이","Article 3 (Personal Data)",
        "제2조(개인정보 처리)","ＩＮＦＯ","A\u0301Ｂ","새\u200d문서",
        "① ㈜ 목적"," 개인정보 \u00a0수집 ","İBİ","𝓐𝓑😀",
        "제1조의2(개인정보)","Article IX Privacy","a\rb","a\nb",
        string.Empty
    };
    for(var k=0;k<500;k++)
        corpus.Add(string.Concat(Enumerable.Range(0,rng.Next(0,12))
            .Select(_=>tokens[rng.Next(tokens.Length)])));
    var differences=0;
    var checkedNormalization=0;
    foreach(var text in corpus)
    foreach(var (mode,sourceName) in methods)
    {
        var method=engineType.GetMethod(sourceName,
            BindingFlags.Static|BindingFlags.NonPublic)!;
        var canonical=(string)method.Invoke(null,new object[]{text})!;
        var native=NativeCodeVersionComparer.NormalizeDocumentText(text,mode);
        Check(native is not null,"Rust normalizer unavailable for supported Unicode");
        if(native!=canonical)
        {
            differences++;
            if(differences<=9)
                Console.WriteLine("NORM_DIFF mode="+mode+" text="+text+
                    " source="+canonical+" rust="+native);
        }
        checkedNormalization++;
    }
    var accepted=(long)engineType.GetProperty("NormalizationRustMatches",
        BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
    var rejected=(long)engineType.GetProperty("NormalizationRustMismatches",
        BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
    Check(differences==0 && accepted>0 && rejected==0,
        "Rust Unicode normalizer not parity-equivalent or native path not accepted");
    Console.WriteLine($"PASS RUST UNICODE NORMALIZATION PARITY {checkedNormalization}/{checkedNormalization}");
}

// Rust-first end-to-end deletion/addition -> replacement decisions.
// Compare all action, text, label, source/target endpoints and positions
// against the preserved managed method on randomized competing candidates.
if (NativeCodeVersionComparer.IsAvailable)
{
    var engine=typeof(NativeComparisonEngine);
    var nativeMarker=engine.GetNestedType("NativeMarker",
        BindingFlags.NonPublic)!;
    var listType=typeof(List<>).MakeGenericType(nativeMarker);
    var original=engine.GetMethod("CoalesceLocationReplacementsCSharp",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var active=engine.GetMethod("CoalesceLocationReplacements",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var markerDel=nativeMarker.GetMethod("Delete",
        BindingFlags.Static|BindingFlags.Public)!;
    var markerAdd=nativeMarker.GetMethod("Insert",
        BindingFlags.Static|BindingFlags.Public)!;
    var markerChange=nativeMarker.GetMethod("Change",
        BindingFlags.Static|BindingFlags.Public)!;
    var matches=engine.GetProperty("ReplacementCoalesceRustMatches",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var mismatches=engine.GetProperty("ReplacementCoalesceRustMismatches",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var beforeMatch=(long)matches.GetValue(null)!;
    var beforeMismatch=(long)mismatches.GetValue(null)!;
    var rng=new Random(20261026);
    var corpusSize=650;
    object? Prop(object o,string name)=>o.GetType().GetProperty(name)!.GetValue(o);
    bool Same(object a,object b)
    {
        foreach(var name in new[]{"Action","Text","CharStart","CharEnd","Part",
            "Message","Pair","PairOrder","ItemOrder","HunkOrder","RelativeDoc",
            "TargetDoc","StructuralNumber"})
            if(!Equals(Prop(a,name),Prop(b,name))) return false;
        var ea=((System.Collections.IEnumerable)Prop(a,"Endpoints")!).Cast<object>()
            .Select(x=>(Prop(x,"TargetDoc"),Prop(x,"CharStart"),Prop(x,"CharEnd")));
        var eb=((System.Collections.IEnumerable)Prop(b,"Endpoints")!).Cast<object>()
            .Select(x=>(Prop(x,"TargetDoc"),Prop(x,"CharStart"),Prop(x,"CharEnd")));
        return ea.SequenceEqual(eb);
    }
    for(var trial=0;trial<corpusSize;trial++)
    {
        var input=(System.Collections.IList)Activator.CreateInstance(listType)!;
        var n=rng.Next(1,10);
        for(var k=0;k<n;k++)
        {
            var oldStart=rng.Next(0,175);
            var oldEnd=oldStart+rng.Next(1,22);
            var newStart=rng.Next(0,175);
            var newEnd=newStart+rng.Next(1,22);
            var item=rng.Next(-1,6);
            var part=rng.Next(6)==0?"header":"body";
            var text="변경 "+rng.Next(0,120)+ (rng.Next(4)==0?"!":"");
            if(rng.Next(2)==0)
                input.Add(markerDel.Invoke(null,new object[]{
                    "A↔B",0,0,1,text,oldStart,oldEnd,newStart,part,item,k}));
            else
                input.Add(markerAdd.Invoke(null,new object[]{
                    "A↔B",0,0,1,text,oldStart,newStart,newEnd,part,item,k}));
        }
        if(trial%7==0)
            input.Add(markerChange.Invoke(null,new object[]{
                "A↔B",0,0,1,"old","new",3,6,10,13,"body",0,77}));
        var reference=((System.Collections.IEnumerable)original.Invoke(null,
            new object[]{input,0,1})!).Cast<object>().ToArray();
        var rust=((System.Collections.IEnumerable)active.Invoke(null,
            new object[]{input,0,1})!).Cast<object>().ToArray();
        Check(reference.Length==rust.Length &&
            reference.Zip(rust).All(x=>Same(x.First,x.Second)),
            "Rust replacement coalescing changed final marker sequence "+trial);
    }
    var afterMatch=(long)matches.GetValue(null)!;
    var afterMismatch=(long)mismatches.GetValue(null)!;
    Check(afterMatch-beforeMatch==corpusSize &&
        afterMismatch==beforeMismatch,
        "Rust replacement decision must be adopted with zero fallback for "+
        corpusSize+" bounded direct regressions: accepted="+
        (afterMatch-beforeMatch)+" mismatches="+(afterMismatch-beforeMismatch));
    Console.WriteLine($"PASS RUST DOCUMENT REPLACEMENT COALESCE CSHARP PARITY {corpusSize}/{corpusSize}");
}

// Rust change-span splitting and Hangul shared-prefix clipping.
// Compare exact pieces and UTF-16 offsets against the original C# code.
if (NativeCodeVersionComparer.IsAvailable)
{
    var owner=typeof(NativeComparisonEngine);
    var csharpPieces=owner.GetMethod("EventPartsCSharp",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var rustPieces=owner.GetMethod("EventParts",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var csharpHangul=owner.GetMethod("TrimSharedHangulPrefixCSharp",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var rustHangul=owner.GetMethod("TrimSharedHangulPrefix",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var acceptedParts=owner.GetProperty("ReviewPiecesRustMatches",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var rejectedParts=owner.GetProperty("ReviewPiecesRustMismatches",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var acceptedTrim=owner.GetProperty("HangulTrimRustMatches",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    var rejectedTrim=owner.GetProperty("HangulTrimRustMismatches",
        BindingFlags.Static|BindingFlags.NonPublic)!;
    long Value(System.Reflection.PropertyInfo prop)=>(long)prop.GetValue(null)!;
    var startParts=Value(acceptedParts);
    var startTrim=Value(acceptedTrim);
    var mismatchedParts=Value(rejectedParts);
    var mismatchedTrim=Value(rejectedTrim);
    var rng=new Random(20261026);
    var texts=new[]{"동의 없이","동의없이","개인정보","법률의 개정 사항",
        "!","?","?",":",";",",","   ","\t","\n","A","B","123",
        "\u00a0","·","가","나","한글",".",", punctuation",")"};
    for(var i=0;i<450;i++)
    {
        var full=string.Concat(Enumerable.Range(0,rng.Next(1,13))
            .Select(_=>texts[rng.Next(texts.Length)]));
        var lo=rng.Next(0,full.Length+4);
        var hi=rng.Next(lo,full.Length+5);
        var canonical=((System.Collections.IEnumerable)csharpPieces.Invoke(null,
            new object[]{full,lo,hi})!).Cast<object>()
            .Select(o=>(Start:(int)o.GetType().GetProperty("Start")!.GetValue(o)!,
                End:(int)o.GetType().GetProperty("End")!.GetValue(o)!,
                Text:(string)o.GetType().GetProperty("Text")!.GetValue(o)!)).ToArray();
        var native=((System.Collections.IEnumerable)rustPieces.Invoke(null,
            new object[]{full,lo,hi})!).Cast<object>()
            .Select(o=>(Start:(int)o.GetType().GetProperty("Start")!.GetValue(o)!,
                End:(int)o.GetType().GetProperty("End")!.GetValue(o)!,
                Text:(string)o.GetType().GetProperty("Text")!.GetValue(o)!)).ToArray();
        Check(canonical.SequenceEqual(native),
            "Rust event sentence punctuation split differs at trial "+i);
    }
    var bases=new[]{"개인정보","개인동의","동의 없이","동의없이",
        "한글문장","개인정보 동의","가나","가나다","계약조항","개인서류",
        "a b","ab","Article 1","제1조","개인\u00a0정보"};
    for(var i=0;i<450;i++)
    {
        var prefix=bases[rng.Next(bases.Length)];
        var old=prefix+texts[rng.Next(texts.Length)];
        var revised=(i%3==0?prefix:bases[rng.Next(bases.Length)])+
            texts[rng.Next(texts.Length)];
        var canonical=(ValueTuple<string,string,int,int>)csharpHangul.Invoke(null,
            new object[]{old,revised})!;
        var native=(ValueTuple<string,string,int,int>)rustHangul.Invoke(null,
            new object[]{old,revised})!;
        Check(canonical.Equals(native),"Rust Hangul shared-prefix offset differs: "+i);
    }
    Check(Value(acceptedParts)-startParts==450 &&
        Value(acceptedTrim)-startTrim==450 &&
        Value(rejectedParts)==mismatchedParts &&
        Value(rejectedTrim)==mismatchedTrim,
        "Rust review piece/trim did not execute on every bounded case or fell back");
    Console.WriteLine("PASS RUST DOCUMENT EVENT PIECES + HANGUL PREFIX CSHARP PARITY 900/900");
}

// Boundary projection parity: word anchors, equal gaps, punctuation,
// Unicode and zero-length insertion/deletion anchors.
if (NativeCodeVersionComparer.IsAvailable)
{
    var engineType=typeof(NativeComparisonEngine);
    var canonical=engineType.GetMethod("MapBoundaryCSharp",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var production=engineType.GetMethod("MapBoundary",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var accepted=engineType.GetProperty("BoundaryRustMatches",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var rejected=engineType.GetProperty("BoundaryRustMismatches",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    long ValueBoundary(System.Reflection.PropertyInfo prop)=>
        (long)prop.GetValue(null)!;
    var before=ValueBoundary(accepted);
    var errors=ValueBoundary(rejected);
    var vectors=new (string A,string B)[]{
        ("The price has changed. New terms apply.","The price has changed! Refunds apply."),
        ("동의없이 개인정보를 수집한다.","동의 없이 개인정보를 수집한다."),
        ("제3조(의무) 회원은 이용료를 지급한다.","제4조(의무) 회원은 수수료를 지급한다."),
        ("abc abc abc","abc x abc"),
        ("어제. 오늘.","어제? 새로운 날."),
        ("A😀B","A😃B"),("","new"),("old",""),("",""),
        ("foo. bar baz","foo! X bar baz"),("© ©","© bar"),
        ("a\nb\nc","a\nx\nc")
    };
    var rng=new Random(20261026);
    var chunks=new[]{"aa","a"," ","\t","제2조","개인정보","동의","없이",
        ".",",","!","?",";","\n","😀","😃","②","B","the","(3)"};
    for(var trial=0;trial<260;trial++)
    {
        string Sample()=>string.Concat(Enumerable.Range(0,rng.Next(1,12))
            .Select(_=>chunks[rng.Next(chunks.Length)]));
        var source=Sample();
        var target=trial%5==0?source:Sample();
        vectors=vectors.Append((source,target)).ToArray();
    }
    var checkedCount=0;var found=0;
    foreach(var (oldText,newText) in vectors)
        for(var x=0;x<=oldText.Length;x++)
        {
            var mappingArgs=new object[]{oldText,newText,x};
            var expected=(int)canonical.Invoke(null,mappingArgs)!;
            var actual=(int)production.Invoke(null,mappingArgs)!;
            if(expected!=actual)
            {
                found++;
                if(found<=5)Console.WriteLine("BOUNDARY_DIFF "+oldText+
                    " → "+newText+" pos="+x+" C#="+expected+" Rust="+actual);
            }
            checkedCount++;
        }
    var adopted=ValueBoundary(accepted)-before;
    var mismatches=ValueBoundary(rejected)-errors;
    Console.WriteLine($"RUST BOUNDARY AUDIT checked={checkedCount} adopted={adopted} mismatch={mismatches}");
    Check(found==0&&mismatches==0&&adopted==checkedCount,
        "Rust MapBoundary still depends on C# for supported documents");
    Console.WriteLine($"PASS RUST BOUNDARY MAPPING CSHARP PARITY {checkedCount}/{checkedCount}");
}

// Rust generic DiffText hunk selection, including offsets for repeated
// English articles, Korean words, punctuation and astral characters.
if (NativeCodeVersionComparer.IsAvailable)
{
    var core=typeof(NativeComparisonEngine);
    var lexer=core.GetMethod("LexTokens",BindingFlags.NonPublic|BindingFlags.Static)!;
    var managed=core.GetMethod("SelectReviewHunksCSharp",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var native=core.GetMethod("SelectReviewHunks",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var accepted=core.GetProperty("DiffHunksRustMatches",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var rejected=core.GetProperty("DiffHunksRustMismatches",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var beforeAccepted=(long)accepted.GetValue(null)!;
    var beforeRejected=(long)rejected.GetValue(null)!;
    var random=new Random(20261026);
    var vocab=new[]{"member","privacy","and","the","동의","없이","개인정보",
        "삭제","추가","제1조",".","!",";",":"," "," ","\n","가","나","⑤","A","B"};
    var cases=0;
    for(var k=0;k<520;k++)
    {
        string MakeTokens()=>string.Concat(Enumerable.Range(0,random.Next(1,18))
            .Select(_=>vocab[random.Next(vocab.Length)]));
        var oldText=MakeTokens();
        var newText=k%7==0?oldText:MakeTokens();
        var aTokens=lexer.Invoke(null,new object[]{oldText})!;
        var bTokens=lexer.Invoke(null,new object[]{newText})!;
        var reference=((System.Collections.IEnumerable)managed.Invoke(null,
            new[]{aTokens,bTokens})!).Cast<object>().ToArray();
        var actual=((System.Collections.IEnumerable)native.Invoke(null,
            new[]{(object)oldText,(object)newText,aTokens,bTokens})!)
            .Cast<object>().ToArray();
        Check(reference.SequenceEqual(actual),
            "Rust DiffText general review hunks differ from C#: "+k+
            " old="+oldText+" new="+newText);
        cases++;
    }
    Check(((long)accepted.GetValue(null)!)-beforeAccepted==cases &&
        ((long)rejected.GetValue(null)!) == beforeRejected,
        "Rust review hunk selector did not accept every supported case");
    Console.WriteLine($"PASS RUST DIFFTEXT GENERIC HUNKS CSHARP PARITY {cases}/{cases}");
}

// Rust full DiffText generic event-generation parity. This is not only
// token matching: compare all produced marker event data and endpoints.
if (NativeCodeVersionComparer.IsAvailable)
{
    var engine=typeof(NativeComparisonEngine);
    var canonical=engine.GetMethod("DiffTextCoreCSharp",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var native=engine.GetMethod("TryRustGenericDiffText",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var compare=engine.GetMethod("SameReplacementMarker",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var accepted=engine.GetProperty("FullDiffRustMatches",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var rejected=engine.GetProperty("FullDiffRustMismatches",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var starting=(long)accepted.GetValue(null)!;
    var failures=(long)rejected.GetValue(null)!;
    var random=new Random(20261026);
    var tokens=new[]{"personal","data","terms","and","the","privacy",
        "payment","개인정보","동의","없이","서비스","제공","수수료",
        ".","!",";",":",","," \n"," "," ","⑥","③","A","B"};
    var failed=0;var checkedEvents=0;
    for(var trial=0;trial<350;trial++)
    {
        string Gen()=>string.Concat(Enumerable.Range(0,random.Next(1,14))
            .Select(_=>tokens[random.Next(tokens.Length)]));
        var oldText=Gen();var newText=trial%11==0?oldText:Gen();
        object[] argv={oldText,newText,5,12,0,1,"A↔B",0,"body",trial%2==0,0};
        var reference=((System.Collections.IEnumerable)canonical.Invoke(null,argv)!)
            .Cast<object>().ToArray();
        var nativeResult=(System.Collections.IEnumerable?)native.Invoke(null,argv);
        if(nativeResult is null)
        {
            failed++;if(failed<=4)Console.WriteLine("RUST_DIFF_UNSUPPORTED "+
                oldText+" → "+newText);
            continue;
        }
        var result=nativeResult.Cast<object>().ToArray();
        if(reference.Length!=result.Length ||
            !reference.Zip(result).All(p=>(bool)compare.Invoke(null,
                new[]{p.First,p.Second})!))
        {
            failed++;if(failed<=4)
                Console.WriteLine("RUST_DIFF_MISMATCH "+oldText+" → "+newText+
                    " count="+reference.Length+"/"+result.Length);
        }
        checkedEvents++;
    }
    var matches=(long)accepted.GetValue(null)!-starting;
    var drifts=(long)rejected.GetValue(null)!-failures;
    Console.WriteLine($"RUST FULL DIFFTEXT AUDIT checked={checkedEvents} accepted={matches} drift={drifts} unsupported={failed}");
    Check(failed==0&&matches==350&&drifts==0,
        "Rust complete generic DiffText changed original event markers");
    Console.WriteLine($"PASS RUST FULL GENERIC DIFFTEXT EVENTS 350/350");
}
var rawPairNative=(long)typeof(NativeComparisonEngine).GetProperty(
    "RawPairRustMatches",BindingFlags.NonPublic|BindingFlags.Static)!
    .GetValue(null)!;
var rawPairUnsupported=(long)typeof(NativeComparisonEngine).GetProperty(
    "RawPairRustUnsupported",BindingFlags.NonPublic|BindingFlags.Static)!
    .GetValue(null)!;
Console.WriteLine($"RUST RAW COMPAREPAIR owned={rawPairNative} unsupported={rawPairUnsupported}");
Check(rawPairNative>0 && rawPairUnsupported==0,
    "Rust-only ComparePair must handle every regression pair without C# fallback");

// Source-owned generic block matching must reproduce exactly the historical
// C# normalization/token similarity and Rust ranking for every returned pair.
if (NativeCodeVersionComparer.IsAvailable)
{
    var rng=new Random(20261026);
    var parts=new[]{"A","the","privacy","contract","personal","data",
        "동의","없이","개인정보","회원","가입","제1조"," "," ",".","!","a_b","123",
        "가나다","네","또는","②","(1)","ＷＯＲＤ"};
    var tested=0;
    for(var trial=0;trial<240;trial++)
    {
        var countA=rng.Next(0,9);var countB=rng.Next(0,9);
        string MakeGenericBody()=>string.Concat(Enumerable.Range(0,rng.Next(1,13))
            .Select(_=>parts[rng.Next(parts.Length)]));
        var old=Enumerable.Range(0,countA).Select(_=>MakeGenericBody()).ToArray();
        var revised=Enumerable.Range(0,countB).Select(_=>MakeGenericBody()).ToArray();
        var similarities=old.SelectMany(a=>revised.Select(b=>
            (double)typeof(NativeComparisonEngine).GetMethod("SimilarityCSharp",
                BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{a,b})!)).ToArray();
        var reference=NativeCodeVersionComparer.MatchGenericDocumentUnits(
            old.Length,revised.Length,similarities);
        var native=NativeCodeVersionComparer.MatchGenericDocumentRaw(old,revised);
        Check(native is not null && reference is not null &&
            native.Count==reference.Count &&
            native.Zip(reference).All(x=>
                x.First.Base==x.Second.Base &&
                x.First.Other==x.Second.Other &&
                x.First.Mode==x.Second.Mode &&
                Math.Abs(x.First.Score-x.Second.Score)<1e-12),
            "Rust source-owned generic matching deviated from reference: "+trial);
        tested++;
    }
    var bigRawBlocks=Enumerable.Range(0,320)
        .Select(i=>$"unique_clause_{i:D3}").ToArray();
    var bigRawMatches=NativeCodeVersionComparer.MatchGenericDocumentRaw(
        bigRawBlocks,bigRawBlocks);
    Check(bigRawMatches is not null && bigRawMatches.Count==320 &&
        bigRawMatches.Select((m,i)=>m.Base==i&&m.Other==i&&m.Score==1.0).All(x=>x),
        "Rust raw matcher must own 320 generic document blocks");
    // A 96-block general document must bypass the legal-article 48-block
    // guard and enter Rust matching directly (not via C# lineage fallback).
    var parsedGeneric=typeof(NativeComparisonEngine).GetMethod(
        "ParseUnits",BindingFlags.Static|BindingFlags.NonPublic)!;
    var routeGeneric=typeof(NativeComparisonEngine).GetMethod(
        "MatchUnitSequence",BindingFlags.Static|BindingFlags.NonPublic)!;
    var directCounter=typeof(NativeComparisonEngine).GetProperty(
        "GenericUnitDirectDispatches",BindingFlags.Static|BindingFlags.NonPublic)!;
    var nativeCounter=typeof(NativeComparisonEngine).GetProperty(
        "GenericUnitRustMatches",BindingFlags.Static|BindingFlags.NonPublic)!;
    var sourceGeneric=string.Join("\n",Enumerable.Range(0,96)
        .Select(i=>$"{i+1}. Section {i:D3}\nUnique clause {i:D3} contents."));
    var rawUnits=parsedGeneric.Invoke(null,new object[]{sourceGeneric,"general"})!;
    var unitCount=((System.Collections.IEnumerable)rawUnits).Cast<object>().Count();
    Check(unitCount==96,"general parser must expose 96 separate blocks");
    var previousDirect=(long)directCounter.GetValue(null)!;
    var previousGenericRust=(long)nativeCounter.GetValue(null)!;
    var matchedGeneric=routeGeneric.Invoke(null,new object[]{
        rawUnits,rawUnits,CancellationToken.None})!;
    Check(((System.Collections.IEnumerable)matchedGeneric).Cast<object>().Count()==96 &&
        (long)directCounter.GetValue(null)!>previousDirect &&
        (long)nativeCounter.GetValue(null)!>previousGenericRust,
        "96-block general document must use direct Rust generic matching");
    var largerBlocks=Enumerable.Range(0,96)
        .Select(i=>$"unique_clause_{i:D3}").ToArray();
    var largerResult=NativeCodeVersionComparer.MatchGenericDocumentRaw(
        largerBlocks,largerBlocks);
    Check(largerResult is not null && largerResult.Count==96 &&
        largerResult.Select((m,i)=>m.Base==i&&m.Other==i&&m.Score==1.0).All(x=>x),
        "Rust raw matcher must own 96 distinct generic blocks");
    Check(NativeCodeVersionComparer.MatchGenericDocumentRaw(
        Enumerable.Range(0,1025).Select(i=>i.ToString()).ToArray(),
        new[]{"one"}) is null,
        "oversize Rust generic input must fail safe");
    Console.WriteLine($"PASS RUST RAW GENERIC UNIT SIMILARITY CSHARP PARITY {tested}/{tested}");
}

// Word XML exporter paragraph, tracked-revision and formatting LCS is Rust-first.
// Both ordinary DP tie-breaking and the long-sequence matcher must be exact.
if (NativeCodeVersionComparer.IsAvailable)
{
    var exporter=typeof(NativeComparisonEngine).Assembly.GetType(
        "DocumentCompare.Avalonia.Engine.NativeOfficeExporter")!;
    var old=exporter.GetMethod("LcsCSharp",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var production=exporter.GetMethod("Lcs",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var accepted=exporter.GetProperty("ExportLcsRustMatches",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var drift=exporter.GetProperty("ExportLcsRustMismatches",
        BindingFlags.NonPublic|BindingFlags.Static)!;
    var start=(long)accepted.GetValue(null)!;
    var rejected=(long)drift.GetValue(null)!;
    var rng=new Random(20261026);
    var words=new[]{"A","B","B","C","동의","개인정보","a-b","123",
        "한글","(1)","①"," ","","same","same","other","표 셀","세션"};
    var tested=0;
    for(var trial=0;trial<275;trial++)
    {
        var first=Enumerable.Range(0,rng.Next(0,23))
            .Select(_=>words[rng.Next(words.Length)]).ToArray();
        var second=Enumerable.Range(0,rng.Next(0,23))
            .Select(_=>words[rng.Next(words.Length)]).ToArray();
        object[] lcsArgs={first,second};
        var expected=(List<(int A,int B)>)old.Invoke(null,lcsArgs)!;
        var actual=(List<(int A,int B)>)production.Invoke(null,lcsArgs)!;
        Check(actual.SequenceEqual(expected),
            "Rust Word exporter LCS differs: "+trial);
        tested++;
    }
    var longDoc=Enumerable.Range(0,2020)
        .Select(i=>"Paragraph "+i).ToArray();
    object[] longArgs={longDoc,longDoc};
    var longExpected=(List<(int A,int B)>)old.Invoke(null,longArgs)!;
    var longActual=(List<(int A,int B)>)production.Invoke(null,longArgs)!;
    Check(longActual.SequenceEqual(longExpected),
        "Rust Word exporter large document block LCS differs");
    tested++;
    Check((long)accepted.GetValue(null)!-start==tested &&
        ((long)drift.GetValue(null)!)==rejected,
        "Word export LCS unexpectedly fell back to C#");
    Console.WriteLine($"PASS RUST WORD EXPORT LCS CSHARP PARITY {tested}/{tested}");
}
var tableMovementType=typeof(NativeComparisonEngine);
var tableMovementAccepted=(long)tableMovementType.GetProperty(
    "TableMovementRustMatches",BindingFlags.Static|BindingFlags.NonPublic)!
    .GetValue(null)!;
var tableMovementMismatch=(long)tableMovementType.GetProperty(
    "TableMovementRustMismatches",BindingFlags.Static|BindingFlags.NonPublic)!
    .GetValue(null)!;
Check(tableMovementAccepted>0&&tableMovementMismatch==0,
    "Rust DOCX table movement must match the exact source correspondence");
Console.WriteLine($"PASS RUST DOCX TABLE MOVEMENT nativeCalls={tableMovementAccepted} mismatches={tableMovementMismatch}");
// Production ownership gates: migrated stages must not silently revert to
// C# implementation during the supported regression document corpus.
var engineOwnership=typeof(NativeComparisonEngine);
long Ownership(string name)=>(long)engineOwnership.GetProperty(name,
    BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
var stageNames=new[]{
    ("UnitParserRustMatches","UnitParserRustMismatches"),
    ("GenericUnitRustMatches","GenericUnitRustMismatches"),
    ("UnitLineageRustMatches","UnitLineageRustMismatches"),
    ("RowAlignmentRustMatches","RowAlignmentRustMismatches"),
    ("TableMovementRustMatches","TableMovementRustMismatches")
};
foreach(var (accept,reject) in stageNames)
{
    var accepted=Ownership(accept);var rejected=Ownership(reject);
    Console.WriteLine($"RUST STAGE OWNERSHIP {accept}={accepted} {reject}={rejected}");
    Check(accepted>0 && rejected==0,
        "migrated source-scored document stage did not adopt Rust exclusively: "+accept);
}
Check(Ownership("SingleUnitRustMatches")>0,
    "one-sided added/deleted document rows must be Rust-owned");
Console.WriteLine("ALL REGRESSIONS PASSED");
