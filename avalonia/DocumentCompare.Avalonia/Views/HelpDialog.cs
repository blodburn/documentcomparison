using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using DocumentCompare.Avalonia.Localization;

namespace DocumentCompare.Avalonia.Views;

public sealed class HelpDialog : Window
{
    public HelpDialog(UiLanguage language)
    {
        Title = UiLocalization.T(language, "도움말 · 간단 사용설명서", "Help · Quick Guide");
        Width = 760;
        Height = 700;
        MinWidth = 620;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var close = new Button
        {
            Content = UiLocalization.T(language, "닫기", "Close"),
            MinWidth = 90,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        close.Click += (_, _) => Close();

        var guide = new TextBlock
        {
            Text = BuildGuide(language),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            LineHeight = 23
        };

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = guide
        };

        var footer = new Border
        {
            Padding = new Thickness(0, 14, 0, 0),
            Child = close
        };
        Grid.SetRow(footer, 1);

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Thickness(22)
        };
        root.Children.Add(scroll);
        root.Children.Add(footer);
        Content = root;
    }

    private static string BuildGuide(UiLanguage language) => language == UiLanguage.Korean
        ? """
문서 비교기 V5.19.4 · 간단 사용설명서

1. 문서 불러오기
A/B/C 제목 영역을 클릭하거나 DOCX/TXT 파일을 해당 영역에 드롭합니다. A와 B는 필수이고 C는 선택입니다.

2. 기준 문서 선택
A/B/C 오른쪽의 ‘기준’을 선택합니다. 기준 문서는 행 정렬과 비교 화면의 기준축으로 사용됩니다.

3. 비교 실행
‘비교 시작’을 누릅니다. ‘비교 방식’은 자동/일반 문서/법률·규정 중 선택할 수 있습니다. 3개 문서를 사용할 때 필요하면 A↔C 추가 비교를 켤 수 있습니다.

4. 변경 표시 읽기
삭제된 내용은 빨간색 취소선, 추가된 내용은 파란색 밑줄로 표시됩니다. 추가/삭제 구간 안의 띄어쓰기도 같은 선으로 이어서 표시됩니다. 번호 [n]은 오른쪽 변경사항의 같은 번호와 연결됩니다.

5. 번호로 위치 맞추기
A/B/C 중 어느 열이든 [n]을 클릭하면 누른 열이 기준열이 됩니다. 다른 열에 같은 [n]이 있으면 같은 세로축으로 맞춰지고, 오른쪽 변경사항도 같은 위치로 이동합니다.

6. 검색 및 특수문자
검색창에서 본문과 변경사항을 찾을 수 있습니다. ‘특수문자 포함’을 끄면 독립적인 문장부호 변경은 비교 결과에서 제외됩니다.

7. 내보내기
‘Excel 내보내기’는 비교 결과를 XLSX로 저장합니다. ‘Word 변경추적’은 변경 전 → 변경 후 조합을 선택해 Word 변경추적 문서를 만듭니다.

8. 언어
프로그램은 Windows 표시 언어를 읽어 한국어 Windows에서는 KR, 그 외 환경에서는 EN으로 시작합니다. 상단 ‘언어/Language’ 메뉴에서 언제든 KR/EN을 바꿀 수 있습니다. 문서 원문은 언어 전환으로 변경되지 않습니다.

Copyright © 2026 blodburn. All rights reserved.
This software is proprietary and is not open source.
"""
        : """
Document Compare V5.19.4 · Quick Guide

1. Load documents
Click the A/B/C header area or drop DOCX/TXT files onto it. A and B are required; C is optional.

2. Choose the base document
Select ‘Base’ on A, B, or C. The base document is used as the alignment/reference axis for the comparison view.

3. Run comparison
Click ‘Compare’. Comparison mode can be Auto, General Document, or Legal/Policy. With three documents, enable the optional A↔C comparison if needed.

4. Read changes
Deleted text is shown with a red strikethrough; inserted text is shown with a blue underline. Spaces inside inserted/deleted phrases are decorated continuously as part of the same edit. Marker [n] corresponds to the same [n] in the Changes column.

5. Align by marker
Click [n] in any A/B/C column. The clicked column becomes the reference. If the other columns contain the same [n], their matching positions are aligned to the same vertical axis, and the Changes pane moves to that marker as well.

6. Search and punctuation
Use Search to find text in the document body or the Changes column. Turning off ‘Include punctuation’ excludes standalone punctuation-only edits.

7. Export
‘Export Excel’ saves the comparison as XLSX. ‘Word Track Changes’ lets you choose an original → revised pair and creates a Word document with tracked changes.

8. Language
The app reads the Windows display language. Korean Windows starts in KR; other environments start in EN. You can switch KR/EN at any time from the Language menu. Switching the UI language never modifies document content.

Copyright © 2026 blodburn. All rights reserved.
This software is proprietary and is not open source.
""";
}
