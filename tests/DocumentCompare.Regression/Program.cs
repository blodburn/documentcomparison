using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using DocumentCompare.Avalonia.Engine;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

const string W="http://schemas.openxmlformats.org/wordprocessingml/2006/main";
var dir=Path.Combine(Path.GetTempPath(), "DocumentCompareRegression", Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir); XNamespace w=W;
static void Check(bool ok,string msg){if(!ok)throw new Exception(msg);}
static void Put(ZipArchive z,string p,string c){var e=z.CreateEntry(p);using var sw=new StreamWriter(e.Open(),new UTF8Encoding(false));sw.Write(c);}
static void Make(string path,string body){using var fs=new FileStream(path,FileMode.Create);using var z=new ZipArchive(fs,ZipArchiveMode.Create);Put(z,"[Content_Types].xml","<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");Put(z,"_rels/.rels","<?xml version=\"1.0\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");Put(z,"word/document.xml","<?xml version=\"1.0\"?><w:document xmlns:w=\""+W+"\"><w:body>"+body+"<w:sectPr/></w:body></w:document>");}
static string P(string t)=>"<w:p><w:r><w:t xml:space=\"preserve\">"+System.Security.SecurityElement.Escape(t)+"</w:t></w:r></w:p>";
static XDocument Doc(string path){using var z=ZipFile.OpenRead(path);using var st=z.GetEntry("word/document.xml")!.Open();return XDocument.Load(st);}
static async Task<string> Read(string path){var t=typeof(NativeComparisonEngine).Assembly.GetType("DocumentCompare.Avalonia.Engine.NativeDocumentReader")!;var m=t.GetMethod("ReadAsync",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!;return await (Task<string>)m.Invoke(null,new object[]{path,CancellationToken.None})!;}
var eng=new NativeComparisonEngine();

// 1. Hidden runs: screen and Word export must use the same visible-text policy.
var ha=Path.Combine(dir,"hiddenA.docx");var hb=Path.Combine(dir,"hiddenB.docx");var ho=Path.Combine(dir,"hiddenOut.docx");
Make(ha,P("Alpha Beta"));Make(hb,"<w:p><w:r><w:t xml:space=\"preserve\">Alpha </w:t></w:r><w:r><w:rPr><w:vanish/></w:rPr><w:t xml:space=\"preserve\">HIDDEN </w:t></w:r><w:r><w:t>Beta</w:t></w:r></w:p>");
var hc=await eng.CompareAsync(new[]{ha,hb},0,"general",true,true);Check(!hc.Rows.Any(r=>r.Changed),"hidden run changed UI");await eng.ExportWordAsync(ha,hb,ho,"T",true,default,hc,0,1);var hd=Doc(ho);Check(!hd.Descendants().Any(x=>x.Name==w+"ins"||x.Name==w+"del"),"hidden run created Word revisions");Console.WriteLine("PASS HIDDEN RUN CONSISTENCY");

string Row(string a,string b)=>"<w:tr><w:tc>"+P(a)+"</w:tc><w:tc>"+P(b)+"</w:tc></w:tr>";
string Tbl(string rows)=>"<w:tbl><w:tblPr/><w:tblGrid><w:gridCol/><w:gridCol/></w:tblGrid>"+rows+"</w:tbl>";
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

Console.WriteLine("ALL REGRESSIONS PASSED");
