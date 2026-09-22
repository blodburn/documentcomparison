using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
using DocumentCompare.Avalonia.Engine;
using DocumentCompare.Avalonia.Models;
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
using(var raceFs=new FileStream(raceWB,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite)){raceFs.Position=Math.Max(0,raceFs.Length-8);raceFs.WriteByte(0x20);}
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


// 105. Format-only DOCX differences must either emit property Track Changes or be rejected.
var fmtA=Path.Combine(dir,"formatOnlyA.docx");var fmtB=Path.Combine(dir,"formatOnlyB.docx");var fmtO=Path.Combine(dir,"formatOnlyOut.docx");
Make(fmtA,"<w:p><w:r><w:rPr><w:b/></w:rPr><w:t>Alpha</w:t></w:r></w:p>");
Make(fmtB,P("Alpha"));
var fmtSafe=false;
try
{
    await eng.ExportWordAsync(fmtA,fmtB,fmtO,"T",true);
    var fmtDoc=Doc(fmtO);
    fmtSafe=fmtDoc.Descendants(w+"rPrChange").Any()||fmtDoc.Descendants(w+"pPrChange").Any();
}
catch(InvalidOperationException){fmtSafe=true;}
Check(fmtSafe,"format-only Word change was silently inherited from B without tracked property change or rejection");
Console.WriteLine("PASS FORMATTING-ONLY EXPORT SAFETY");

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


// 112-115. Non-paragraph formatting must not silently inherit from physical base B.
static async Task<bool> FormattingExportSafe(NativeComparisonEngine engine,string a,string b,string o,XNamespace w,string trackedName)
{
    try
    {
        await engine.ExportWordAsync(a,b,o,"T",true);
        var doc=Doc(o);
        return doc.Descendants(w+trackedName).Any();
    }
    catch(InvalidOperationException){return true;}
}

var tblFmtA=Path.Combine(dir,"tableFmtA.docx");var tblFmtB=Path.Combine(dir,"tableFmtB.docx");var tblFmtO=Path.Combine(dir,"tableFmtOut.docx");
Make(tblFmtA,"<w:tbl><w:tblPr><w:tblW w:w='5000' w:type='dxa'/></w:tblPr><w:tblGrid><w:gridCol/></w:tblGrid><w:tr><w:tc><w:tcPr/><w:p><w:r><w:t>Alpha</w:t></w:r></w:p></w:tc></w:tr></w:tbl>");
Make(tblFmtB,"<w:tbl><w:tblPr><w:tblW w:w='6000' w:type='dxa'/></w:tblPr><w:tblGrid><w:gridCol/></w:tblGrid><w:tr><w:tc><w:tcPr/><w:p><w:r><w:t>Alpha</w:t></w:r></w:p></w:tc></w:tr></w:tbl>");
var tblFmtSafe=await FormattingExportSafe(eng,tblFmtA,tblFmtB,tblFmtO,w,"tblPrChange");
Console.WriteLine("REVIEW TABLE-FORMATTING SAFETY="+tblFmtSafe);

var cellFmtA=Path.Combine(dir,"cellFmtA.docx");var cellFmtB=Path.Combine(dir,"cellFmtB.docx");var cellFmtO=Path.Combine(dir,"cellFmtOut.docx");
Make(cellFmtA,"<w:tbl><w:tblPr/><w:tblGrid><w:gridCol/></w:tblGrid><w:tr><w:tc><w:tcPr><w:shd w:fill='FFFFFF'/></w:tcPr><w:p><w:r><w:t>Alpha</w:t></w:r></w:p></w:tc></w:tr></w:tbl>");
Make(cellFmtB,"<w:tbl><w:tblPr/><w:tblGrid><w:gridCol/></w:tblGrid><w:tr><w:tc><w:tcPr><w:shd w:fill='EEEEEE'/></w:tcPr><w:p><w:r><w:t>Alpha</w:t></w:r></w:p></w:tc></w:tr></w:tbl>");
var cellFmtSafe=await FormattingExportSafe(eng,cellFmtA,cellFmtB,cellFmtO,w,"tcPrChange");
Console.WriteLine("REVIEW CELL-FORMATTING SAFETY="+cellFmtSafe);

var sectFmtA=Path.Combine(dir,"sectionFmtA.docx");var sectFmtB=Path.Combine(dir,"sectionFmtB.docx");var sectFmtO=Path.Combine(dir,"sectionFmtOut.docx");
Make(sectFmtA,P("Alpha"));
Make(sectFmtB,P("Alpha"));
AddWordXml(sectFmtA,"word/document.xml","<w:document xmlns:w='"+W+"'><w:body>"+P("Alpha")+"<w:sectPr><w:pgMar w:top='1440' w:right='1440' w:bottom='1440' w:left='1440'/></w:sectPr></w:body></w:document>");
AddWordXml(sectFmtB,"word/document.xml","<w:document xmlns:w='"+W+"'><w:body>"+P("Alpha")+"<w:sectPr><w:pgMar w:top='720' w:right='1440' w:bottom='1440' w:left='1440'/></w:sectPr></w:body></w:document>");
var sectFmtSafe=await FormattingExportSafe(eng,sectFmtA,sectFmtB,sectFmtO,w,"sectPrChange");
Console.WriteLine("REVIEW SECTION-FORMATTING SAFETY="+sectFmtSafe);

var styleFmtA=Path.Combine(dir,"styleFmtA.docx");var styleFmtB=Path.Combine(dir,"styleFmtB.docx");var styleFmtO=Path.Combine(dir,"styleFmtOut.docx");
Make(styleFmtA,"<w:p><w:pPr><w:pStyle w:val='MyStyle'/></w:pPr><w:r><w:t>Alpha</w:t></w:r></w:p>");
Make(styleFmtB,"<w:p><w:pPr><w:pStyle w:val='MyStyle'/></w:pPr><w:r><w:t>Alpha</w:t></w:r></w:p>");
AddWordXml(styleFmtA,"word/styles.xml","<w:styles xmlns:w='"+W+"'><w:style w:type='paragraph' w:styleId='MyStyle'><w:name w:val='MyStyle'/><w:rPr><w:b/></w:rPr></w:style></w:styles>");
AddWordXml(styleFmtB,"word/styles.xml","<w:styles xmlns:w='"+W+"'><w:style w:type='paragraph' w:styleId='MyStyle'><w:name w:val='MyStyle'/><w:rPr><w:i/></w:rPr></w:style></w:styles>");
var styleFmtSafe=false;
try{await eng.ExportWordAsync(styleFmtA,styleFmtB,styleFmtO,"T",true);}
catch(InvalidOperationException){styleFmtSafe=true;}
Console.WriteLine("REVIEW REFERENCED-STYLE SAFETY="+styleFmtSafe);

Check(tblFmtSafe,"table property change was silently inherited without tracked change or rejection");
Console.WriteLine("PASS TABLE-FORMATTING EXPORT SAFETY");
Check(cellFmtSafe,"cell property change was silently inherited without tracked change or rejection");
Console.WriteLine("PASS CELL-FORMATTING EXPORT SAFETY");
Check(sectFmtSafe,"section property change was silently inherited without tracked change or rejection");
Console.WriteLine("PASS SECTION-FORMATTING EXPORT SAFETY");
Check(styleFmtSafe,"referenced style change was silently inherited without rejection");
Console.WriteLine("PASS REFERENCED-STYLE EXPORT SAFETY");


// 116. Default paragraph style changes affect unstyled surviving paragraphs and must be guarded.
var defaultStyleA=Path.Combine(dir,"defaultStyleA.docx");var defaultStyleB=Path.Combine(dir,"defaultStyleB.docx");var defaultStyleO=Path.Combine(dir,"defaultStyleOut.docx");
Make(defaultStyleA,P("Alpha"));Make(defaultStyleB,P("Alpha"));
AddWordXml(defaultStyleA,"word/styles.xml","<w:styles xmlns:w='"+W+"'><w:style w:type='paragraph' w:default='1' w:styleId='Normal'><w:name w:val='Normal'/><w:rPr><w:b/></w:rPr></w:style></w:styles>");
AddWordXml(defaultStyleB,"word/styles.xml","<w:styles xmlns:w='"+W+"'><w:style w:type='paragraph' w:default='1' w:styleId='Normal'><w:name w:val='Normal'/><w:rPr><w:i/></w:rPr></w:style></w:styles>");
var defaultStyleSafe=false;
try{await eng.ExportWordAsync(defaultStyleA,defaultStyleB,defaultStyleO,"T",true);}
catch(InvalidOperationException){defaultStyleSafe=true;}
Check(defaultStyleSafe,"default paragraph style change silently altered unstyled surviving text");
Console.WriteLine("PASS DEFAULT-STYLE EXPORT SAFETY");

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


// 116. A used table style definition change must not silently inherit from B.
var tblStyleA=Path.Combine(dir,"tblStyleA.docx");var tblStyleB=Path.Combine(dir,"tblStyleB.docx");var tblStyleO=Path.Combine(dir,"tblStyleOut.docx");
var styledTable="<w:tbl><w:tblPr><w:tblStyle w:val='GridStyle'/></w:tblPr><w:tblGrid><w:gridCol/></w:tblGrid><w:tr><w:tc><w:tcPr/><w:p><w:r><w:t>Alpha</w:t></w:r></w:p></w:tc></w:tr></w:tbl>";
Make(tblStyleA,styledTable);Make(tblStyleB,styledTable);
AddWordXml(tblStyleA,"word/styles.xml","<w:styles xmlns:w='"+W+"'><w:style w:type='table' w:styleId='GridStyle'><w:name w:val='GridStyle'/><w:tblPr><w:tblBorders><w:top w:val='single' w:sz='4'/></w:tblBorders></w:tblPr></w:style></w:styles>");
AddWordXml(tblStyleB,"word/styles.xml","<w:styles xmlns:w='"+W+"'><w:style w:type='table' w:styleId='GridStyle'><w:name w:val='GridStyle'/><w:tblPr><w:tblBorders><w:top w:val='double' w:sz='8'/></w:tblBorders></w:tblPr></w:style></w:styles>");
var tblStyleSafe=false;
try{await eng.ExportWordAsync(tblStyleA,tblStyleB,tblStyleO,"T",true);}
catch(InvalidOperationException){tblStyleSafe=true;}
Check(tblStyleSafe,"used table style definition change was silently inherited");
Console.WriteLine("PASS TABLE-STYLE DEFINITION SAFETY");

// 117. Theme palette changes can alter themeColor/themeFont rendering without changing document.xml.
var themeFmtA=Path.Combine(dir,"themeFmtA.docx");var themeFmtB=Path.Combine(dir,"themeFmtB.docx");var themeFmtO=Path.Combine(dir,"themeFmtOut.docx");
var themeBody="<w:p><w:r><w:rPr><w:color w:themeColor='accent1'/></w:rPr><w:t>Alpha</w:t></w:r></w:p>";
Make(themeFmtA,themeBody);Make(themeFmtB,themeBody);
const string A_NS="http://schemas.openxmlformats.org/drawingml/2006/main";
AddWordXml(themeFmtA,"word/theme/theme1.xml","<a:theme xmlns:a='"+A_NS+"' name='T'><a:themeElements><a:clrScheme name='C'><a:dk1><a:srgbClr val='000000'/></a:dk1><a:lt1><a:srgbClr val='FFFFFF'/></a:lt1><a:accent1><a:srgbClr val='FF0000'/></a:accent1></a:clrScheme></a:themeElements></a:theme>");
AddWordXml(themeFmtB,"word/theme/theme1.xml","<a:theme xmlns:a='"+A_NS+"' name='T'><a:themeElements><a:clrScheme name='C'><a:dk1><a:srgbClr val='000000'/></a:dk1><a:lt1><a:srgbClr val='FFFFFF'/></a:lt1><a:accent1><a:srgbClr val='0000FF'/></a:accent1></a:clrScheme></a:themeElements></a:theme>");
var themeFmtSafe=false;
try{await eng.ExportWordAsync(themeFmtA,themeFmtB,themeFmtO,"T",true);}
catch(InvalidOperationException){themeFmtSafe=true;}
Check(themeFmtSafe,"theme change affecting themeColor was silently inherited");
Console.WriteLine("PASS THEME-FORMATTING SAFETY");

// 118. Number labels may stay identical while numbering-level layout formatting changes.
var numFmtA=Path.Combine(dir,"numFmtA.docx");var numFmtB=Path.Combine(dir,"numFmtB.docx");var numFmtO=Path.Combine(dir,"numFmtOut.docx");
var numberedP="<w:p><w:pPr><w:numPr><w:ilvl w:val='0'/><w:numId w:val='1'/></w:numPr></w:pPr><w:r><w:t>Alpha</w:t></w:r></w:p>";
Make(numFmtA,numberedP);Make(numFmtB,numberedP);
AddWordXml(numFmtA,"word/numbering.xml","<w:numbering xmlns:w='"+W+"'><w:abstractNum w:abstractNumId='1'><w:lvl w:ilvl='0'><w:start w:val='1'/><w:numFmt w:val='decimal'/><w:lvlText w:val='%1.'/><w:pPr><w:ind w:left='720' w:hanging='360'/></w:pPr></w:lvl></w:abstractNum><w:num w:numId='1'><w:abstractNumId w:val='1'/></w:num></w:numbering>");
AddWordXml(numFmtB,"word/numbering.xml","<w:numbering xmlns:w='"+W+"'><w:abstractNum w:abstractNumId='1'><w:lvl w:ilvl='0'><w:start w:val='1'/><w:numFmt w:val='decimal'/><w:lvlText w:val='%1.'/><w:pPr><w:ind w:left='1440' w:hanging='360'/></w:pPr></w:lvl></w:abstractNum><w:num w:numId='1'><w:abstractNumId w:val='1'/></w:num></w:numbering>");
var numFmtSafe=false;
try{await eng.ExportWordAsync(numFmtA,numFmtB,numFmtO,"T",true);}
catch(InvalidOperationException){numFmtSafe=true;}
Check(numFmtSafe,"numbering layout formatting change was silently inherited");
Console.WriteLine("PASS NUMBERING-FORMATTING SAFETY");

// 121. A theme change that is not referenced by surviving document formatting must not
// falsely block tracked export.
var unusedThemeA=Path.Combine(dir,"unusedThemeA.docx");var unusedThemeB=Path.Combine(dir,"unusedThemeB.docx");var unusedThemeO=Path.Combine(dir,"unusedThemeOut.docx");
Make(unusedThemeA,P("Alpha"));Make(unusedThemeB,P("Alpha"));
AddWordXml(unusedThemeA,"word/theme/theme1.xml","<a:theme xmlns:a='"+A_NS+"' name='T'><a:themeElements><a:clrScheme name='C'><a:dk1><a:srgbClr val='000000'/></a:dk1><a:lt1><a:srgbClr val='FFFFFF'/></a:lt1><a:accent1><a:srgbClr val='FF0000'/></a:accent1></a:clrScheme></a:themeElements></a:theme>");
AddWordXml(unusedThemeB,"word/theme/theme1.xml","<a:theme xmlns:a='"+A_NS+"' name='T'><a:themeElements><a:clrScheme name='C'><a:dk1><a:srgbClr val='000000'/></a:dk1><a:lt1><a:srgbClr val='FFFFFF'/></a:lt1><a:accent1><a:srgbClr val='0000FF'/></a:accent1></a:clrScheme></a:themeElements></a:theme>");
await eng.ExportWordAsync(unusedThemeA,unusedThemeB,unusedThemeO,"T",true);
Check(File.Exists(unusedThemeO),"unused theme change was falsely blocked");
Console.WriteLine("PASS UNUSED-THEME CHANGE ALLOWED");

// 122. Replacing one non-empty numPr with another while the rendered label stays the same
// must not be silently inherited from B. ApplyTrackedNumberingDiff only emits a revision when
// the visible label changes.
var numRefA=Path.Combine(dir,"numRefA.docx");var numRefB=Path.Combine(dir,"numRefB.docx");var numRefO=Path.Combine(dir,"numRefOut.docx");
var numRefPA="<w:p><w:pPr><w:numPr><w:ilvl w:val='0'/><w:numId w:val='1'/></w:numPr></w:pPr><w:r><w:t>Alpha</w:t></w:r></w:p>";
var numRefPB="<w:p><w:pPr><w:numPr><w:ilvl w:val='0'/><w:numId w:val='2'/></w:numPr></w:pPr><w:r><w:t>Alpha</w:t></w:r></w:p>";
Make(numRefA,numRefPA);Make(numRefB,numRefPB);
var sameNumbering="<w:numbering xmlns:w='"+W+"'><w:abstractNum w:abstractNumId='1'><w:lvl w:ilvl='0'><w:start w:val='1'/><w:numFmt w:val='decimal'/><w:lvlText w:val='%1.'/></w:lvl></w:abstractNum><w:num w:numId='1'><w:abstractNumId w:val='1'/></w:num><w:num w:numId='2'><w:abstractNumId w:val='1'/></w:num></w:numbering>";
AddWordXml(numRefA,"word/numbering.xml",sameNumbering);AddWordXml(numRefB,"word/numbering.xml",sameNumbering);
var numRefBlocked=false;
try{await eng.ExportWordAsync(numRefA,numRefB,numRefO,"T",true);}
catch(InvalidOperationException){numRefBlocked=true;}
Check(numRefBlocked,"same-label non-empty numPr replacement was silently inherited from B");
Console.WriteLine("PASS SAME-LABEL NUMPR REPLACEMENT SAFETY");

// 123. Deleting a page/column/clear break must not silently degrade to an ordinary manual break.
var pageBreakA=Path.Combine(dir,"pageBreakA.docx");var pageBreakB=Path.Combine(dir,"pageBreakB.docx");var pageBreakO=Path.Combine(dir,"pageBreakOut.docx");
Make(pageBreakA,"<w:p><w:r><w:t>Alpha</w:t><w:br w:type='page'/><w:t>Beta</w:t></w:r></w:p>");
Make(pageBreakB,P("AlphaBeta"));
var pageBreakBlocked=false;
try{await eng.ExportWordAsync(pageBreakA,pageBreakB,pageBreakO,"T",true);}
catch(InvalidOperationException){pageBreakBlocked=true;}
Check(pageBreakBlocked,"page break deletion degraded to an ordinary manual break");
Console.WriteLine("PASS SPECIAL-BREAK DELETION SAFETY");

Console.WriteLine("ALL REGRESSIONS PASSED");
