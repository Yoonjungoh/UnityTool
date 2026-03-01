#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// [에디터 전용] Google Sheet 구조를 읽어 런타임 코드를 자동 생성합니다.
///
/// ★ 생성 파일 목록
///   SheetEnums.cs          → enum 정의
///   MetaData.cs            → 모든 XxxMetaData 클래스
///   XxxMetaDataSO.cs       → ScriptableObject 구조 정의
///   GoogleSheetConfig.cs   → SpreadsheetId / GID / URL 빌더
///   SpecDataManager.cs     → 런타임 다운로드 + 파싱 + Get API
///
/// ★ 런타임 흐름
///   Managers.Start()
///     → CoSpecDataManagerInit()
///       → SpecData.CoDownloadDataSheet()
///         → CoFetch_Currency() / CoFetch_Monster() (순차)
///           → ParseGvizJson_Xxx() → Dict/List 캐시
///         → IsReady = true
/// </summary>
public class GoogleSheetCodeGenerator : EditorWindow
{
    // ══════════════════════════════════════════
    // ★ 설정 (여기만 수정)
    // ══════════════════════════════════════════
    private const string SPREADSHEET_ID = "15DaZH8xH5lCG-37xep0nZ66eFRW9H04uSKYgUccrtXo";

    private static readonly SheetInfo[] SHEETS =
    {
        new SheetInfo { sheetName = "Currency", gid = "0"          },
        new SheetInfo { sheetName = "Monster",  gid = "1375711091" },
    };

    // ★ 프로젝트 폴더 구조에 맞게 수정
    private const string OUTPUT_DATA_PATH    = "Assets/Scripts/Data/";           // MetaData.cs, SheetEnums.cs, GoogleSheetConfig.cs
    private const string OUTPUT_SO_PATH      = "Assets/Scripts/Data/SO/";        // XxxMetaDataSO.cs
    private const string OUTPUT_MANAGER_PATH = "Assets/Scripts/Managers/Core/";  // SpecDataManager.cs
    // ══════════════════════════════════════════

    private bool    _isRunning;
    private string  _log = "버튼을 눌러 시작하세요.\n\n" +
                           "※ 스프레드시트 공개 설정: 링크 있는 모든 사용자 - 뷰어\n" +
                           "※ 컬럼 구조를 읽어 런타임 코드를 생성합니다.\n" +
                           "※ 데이터는 런타임에 Google Sheets에서 실시간으로 받아옵니다.";
    private Vector2 _scroll;

    private readonly Dictionary<string, SheetParseResult> _results = new();
    private Queue<SheetInfo> _queue;
    private UnityWebRequest  _req;
    private SheetInfo        _cur;

    [MenuItem("Tools/Spec Data Generator")]
    public static void Open() => GetWindow<GoogleSheetCodeGenerator>("Spec Data Generator").Show();

    // ──────────────────────────────────────────
    // GUI
    // ──────────────────────────────────────────
    private void OnGUI()
    {
        GUILayout.Label("Spec Data 코드 자동 생성기", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);

        EditorGUILayout.HelpBox(
            "생성 파일:\n" +
            "  • SheetEnums.cs          enum 정의\n" +
            "  • MetaData.cs            모든 XxxMetaData 클래스\n" +
            "  • XxxMetaDataSO.cs       ScriptableObject 구조\n" +
            "  • GoogleSheetConfig.cs   SpreadsheetId / GID / URL 설정\n" +
            "  • SpecDataManager.cs     런타임 다운로드 + 파싱 + Get API\n\n" +
            "컬럼 구조 변경 시에만 재실행하면 됩니다.\n" +
            "데이터 변경은 앱 재시작만으로 즉시 반영됩니다.",
            MessageType.Info);

        EditorGUILayout.Space(6);
        GUI.enabled = !_isRunning;
        GUI.backgroundColor = new Color(0.3f, 0.85f, 0.45f);
        if (GUILayout.Button("▶  코드 자동 생성", GUILayout.Height(44)))
            StartPipeline();
        GUI.backgroundColor = Color.white;
        GUI.enabled = true;

        EditorGUILayout.Space(8);
        GUILayout.Label("로그", EditorStyles.boldLabel);
        _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
        EditorGUILayout.TextArea(_log, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

    // ──────────────────────────────────────────
    // 파이프라인
    // ──────────────────────────────────────────
    private void StartPipeline()
    {
        _isRunning = true;
        _log = "";
        _results.Clear();
        Log("=== 시트 구조 분석 시작 ===");
        _queue = new Queue<SheetInfo>(SHEETS);
        EditorApplication.update += Tick;
    }

    private void Tick()
    {
        if (_req != null)
        {
            if (!_req.isDone) return;

            if (_req.result == UnityWebRequest.Result.Success)
            {
                Log($"  ✓ 구조 파싱 완료: {_cur.sheetName}");
                _results[_cur.sheetName] = ParseStructureOnly(_cur.sheetName, _req.downloadHandler.text);
            }
            else
            {
                Log($"  [오류] {_cur.sheetName}: {_req.error}");
            }
            _req.Dispose();
            _req = null;
        }

        if (_queue.Count > 0)
        {
            _cur = _queue.Dequeue();
            // 에디터에서 컬럼 구조 파악용으로만 CSV를 1회 사용
            // 런타임에서는 gviz JSON API를 직접 호출
            string url = $"https://docs.google.com/spreadsheets/d/{SPREADSHEET_ID}/export?format=csv&gid={_cur.gid}";
            Log($"구조 분석 중: {_cur.sheetName} ...");
            _req = UnityWebRequest.Get(url);
            _req.SendWebRequest();
            return;
        }

        EditorApplication.update -= Tick;
        GenerateAllCode();
    }

    // ──────────────────────────────────────────
    // 코드 생성 총괄
    // ──────────────────────────────────────────
    private void GenerateAllCode()
    {
        Log("\n=== 코드 생성 시작 ===");

        EnsureDir(OUTPUT_DATA_PATH);
        EnsureDir(OUTPUT_SO_PATH);
        EnsureDir(OUTPUT_MANAGER_PATH);

        GenerateEnumFile();
        GenerateMetaDataFile();
        GenerateSOFiles();
        GenerateSheetConfig();
        GenerateSpecDataManager();

        AssetDatabase.Refresh();

        _isRunning = false;
        Log("\n✅ 코드 생성 완료!");
        Log("");
        Log("[ 런타임 흐름 ]");
        Log("  Managers.Start()");
        Log("    → CoSpecDataManagerInit()");
        Log("      → SpecData.CoDownloadDataSheet()");
        foreach (var name in _results.Keys)
            Log($"        → CoFetch_{name}() → ParseGvizJson_{name}()");
        Log("      → IsReady = true");
        Log("");
        Log("[ 사용 예시 ]");
        Log("  if (!Managers.SpecData.IsReady) yield return new WaitUntil(...);");
        foreach (var name in _results.Keys)
            Log($"  Managers.SpecData.Get{name}(id)");
        Repaint();
    }

    // ──────────────────────────────────────────
    // 1. SheetEnums.cs
    // ──────────────────────────────────────────
    private void GenerateEnumFile()
    {
        var sb = new StringBuilder();
        AppendFileHeader(sb);
        sb.AppendLine();

        bool any = false;
        var written = new HashSet<string>();

        foreach (var kv in _results)
        {
            foreach (var col in kv.Value.Columns)
            {
                if (col.TypeHint != "enum") continue;
                if (!written.Add(col.FieldName)) continue;

                any = true;
                sb.AppendLine($"public enum {col.FieldName}");
                sb.AppendLine("{");
                sb.AppendLine("    None = 0,");

                int idx  = kv.Value.Columns.IndexOf(col);
                var seen = new HashSet<string> { "None" };

                foreach (var row in kv.Value.SampleRows)
                {
                    string val = idx < row.Count ? row[idx].Trim() : "";
                    if (!string.IsNullOrEmpty(val) && seen.Add(val))
                        sb.AppendLine($"    {val},");
                }

                sb.AppendLine("}");
                sb.AppendLine();
            }
        }

        if (!any) { Log("  (enum 없음, SheetEnums.cs 스킵)"); return; }
        WriteFile(OUTPUT_DATA_PATH + "SheetEnums.cs", sb.ToString());
    }

    // ──────────────────────────────────────────
    // 2. MetaData.cs
    // ──────────────────────────────────────────
    private void GenerateMetaDataFile()
    {
        var sb = new StringBuilder();
        AppendFileHeader(sb);
        sb.AppendLine("using System;");
        sb.AppendLine();
        sb.AppendLine("// 모든 MetaData 클래스는 이 파일에서 통합 관리합니다.");
        sb.AppendLine("// 컬럼 구조가 바뀌면 에디터 툴을 재실행하세요.");

        foreach (var kv in _results)
        {
            string name = kv.Value.SheetName;
            sb.AppendLine();
            sb.AppendLine($"// ── {name} ─────────────────────────────────────────");
            sb.AppendLine("[Serializable]");
            sb.AppendLine($"public class {name}MetaData");
            sb.AppendLine("{");
            foreach (var col in kv.Value.Columns)
                sb.AppendLine($"    public {HintToCsType(col.TypeHint, col.FieldName)} {col.FieldName};");
            sb.AppendLine("}");
        }

        WriteFile(OUTPUT_DATA_PATH + "MetaData.cs", sb.ToString());
    }

    // ──────────────────────────────────────────
    // 3. XxxMetaDataSO.cs (구조 확인용)
    // ──────────────────────────────────────────
    private void GenerateSOFiles()
    {
        foreach (var kv in _results)
        {
            string name = kv.Value.SheetName;
            var sb = new StringBuilder();
            AppendFileHeader(sb);
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using UnityEngine;");
            sb.AppendLine();
            sb.AppendLine($"[CreateAssetMenu(fileName = \"{name}MetaDataSO\", menuName = \"SpecData/{name}MetaDataSO\")]");
            sb.AppendLine($"public class {name}MetaDataSO : ScriptableObject");
            sb.AppendLine("{");
            sb.AppendLine($"    public List<{name}MetaData> rows = new List<{name}MetaData>();");
            sb.AppendLine("}");

            WriteFile(OUTPUT_SO_PATH + $"{name}MetaDataSO.cs", sb.ToString());
        }
    }

    // ──────────────────────────────────────────
    // 4. GoogleSheetConfig.cs
    // ──────────────────────────────────────────
    private void GenerateSheetConfig()
    {
        var sb = new StringBuilder();
        AppendFileHeader(sb);
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Google Sheets 연결 설정. SpecDataManager가 런타임에 참조합니다.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class GoogleSheetConfig");
        sb.AppendLine("{");
        sb.AppendLine($"    public const string SpreadsheetId = \"{SPREADSHEET_ID}\";");
        sb.AppendLine();
        sb.AppendLine("    public static readonly Dictionary<string, string> SheetGids =");
        sb.AppendLine("        new Dictionary<string, string>");
        sb.AppendLine("    {");
        foreach (var sheet in SHEETS)
            sb.AppendLine($"        {{ \"{sheet.sheetName}\", \"{sheet.gid}\" }},");
        sb.AppendLine("    };");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Google Visualization JSON API URL (공개 시트, API Key 불필요)");
        sb.AppendLine("    /// 응답 형식: JSONP → JSONP 래퍼 제거 후 파싱");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public static string BuildJsonUrl(string gid)");
        sb.AppendLine("    {");
        sb.AppendLine("        return string.Format(");
        sb.AppendLine("            \"https://docs.google.com/spreadsheets/d/{0}/gviz/tq?tqx=out:json&gid={1}\",");
        sb.AppendLine("            SpreadsheetId, gid);");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        WriteFile(OUTPUT_DATA_PATH + "GoogleSheetConfig.cs", sb.ToString());
    }

    // ──────────────────────────────────────────
    // 5. SpecDataManager.cs (런타임 핵심)
    // ──────────────────────────────────────────
    private void GenerateSpecDataManager()
    {
        var sb = new StringBuilder();
        AppendFileHeader(sb);
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using UnityEngine;");
        sb.AppendLine("using UnityEngine.Networking;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Google Sheets JSON API를 런타임에 직접 호출하여 MetaData를 파싱합니다.");
        sb.AppendLine("/// 빌드 후에도 시트 데이터 변경이 즉시 반영됩니다 (재빌드 불필요).");
        sb.AppendLine("///");
        sb.AppendLine("/// [호출 순서]");
        sb.AppendLine("/// Managers.CoSpecDataManagerInit()");
        sb.AppendLine("///   → StartCoroutine(SpecData.CoDownloadDataSheet())");
        sb.AppendLine("///     → 각 시트 순차 다운로드 + 파싱");
        sb.AppendLine("///     → IsReady = true");
        sb.AppendLine("///");
        sb.AppendLine("/// [사용 예]");
        sb.AppendLine("/// MonsterMetaData data = Managers.SpecData.GetMonster(1);");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public class SpecDataManager");
        sb.AppendLine("{");

        // ── 상태
        sb.AppendLine("    // ── 상태 ──────────────────────────────────────────────────");
        sb.AppendLine("    public bool IsReady { get; private set; }");
        sb.AppendLine();

        // ── 저장소 선언
        sb.AppendLine("    // ── 데이터 저장소 ─────────────────────────────────────────");
        foreach (var name in _results.Keys)
        {
            sb.AppendLine($"    Dictionary<int, {name}MetaData> _{LowerFirst(name)}Dict = new Dictionary<int, {name}MetaData>();");
            sb.AppendLine($"    List<{name}MetaData>            _{LowerFirst(name)}List = new List<{name}MetaData>();");
        }
        sb.AppendLine();

        // ── CoDownloadDataSheet (메인 진입점)
        sb.AppendLine("    // ═══════════════════════════════════════════════════════════");
        sb.AppendLine("    // 메인 다운로드 코루틴");
        sb.AppendLine("    // Managers.CoSpecDataManagerInit()에서 StartCoroutine으로 호출");
        sb.AppendLine("    // ═══════════════════════════════════════════════════════════");
        sb.AppendLine("    public IEnumerator CoDownloadDataSheet()");
        sb.AppendLine("    {");
        sb.AppendLine("        IsReady = false;");
        sb.AppendLine("        Debug.Log(\"[SpecDataManager] 데이터 다운로드 시작\");");
        sb.AppendLine();
        sb.AppendLine("        // 시트를 순차적으로 다운로드 + 파싱");
        sb.AppendLine("        // (yield return으로 하나씩 완료 후 다음으로 넘어감)");

        foreach (var name in _results.Keys)
            sb.AppendLine($"        yield return CoFetch_{name}();");

        sb.AppendLine();
        sb.AppendLine("        IsReady = true;");
        sb.AppendLine("        Debug.Log(\"[SpecDataManager] 모든 데이터 로드 완료\");");
        sb.AppendLine("    }");
        sb.AppendLine();

        // ── 시트별 CoFetch 코루틴
        sb.AppendLine("    // ── 시트별 fetch 코루틴 ──────────────────────────────────");
        foreach (var kv in _results)
        {
            string name = kv.Value.SheetName;
            var    cols = kv.Value.Columns;

            sb.AppendLine($"    IEnumerator CoFetch_{name}()");
            sb.AppendLine("    {");
            sb.AppendLine($"        string url = GoogleSheetConfig.BuildJsonUrl(");
            sb.AppendLine($"            GoogleSheetConfig.SheetGids[\"{name}\"]);");
            sb.AppendLine();
            sb.AppendLine("        using (UnityWebRequest req = UnityWebRequest.Get(url))");
            sb.AppendLine("        {");
            sb.AppendLine("            yield return req.SendWebRequest();");
            sb.AppendLine();
            sb.AppendLine("            if (req.result != UnityWebRequest.Result.Success)");
            sb.AppendLine("            {");
            sb.AppendLine($"                Debug.LogError(\"[SpecDataManager] {name} 다운로드 실패: \" + req.error);");
            sb.AppendLine("                yield break;");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine($"            _{LowerFirst(name)}Dict.Clear();");
            sb.AppendLine($"            _{LowerFirst(name)}List.Clear();");
            sb.AppendLine();
            sb.AppendLine("            // GvizParser: rows[0]=타입힌트, rows[1]=헤더, rows[2~]=데이터");
            sb.AppendLine($"            // colCount:{cols.Count} → 0값 셀 생략 등으로 배열이 짧아지는 경우를 패딩으로 보완");
            sb.AppendLine($"            List<string[]> rows = GvizParser.Parse(req.downloadHandler.text, colCount: {cols.Count});");
            sb.AppendLine($"            for (int i = 2; i < rows.Count; i++)");
            sb.AppendLine("            {");
            sb.AppendLine("                string[] cells = rows[i];");
            sb.AppendLine("                // Id 셀이 비어있으면 빈 행 → 스킵");
            sb.AppendLine("                if (string.IsNullOrEmpty(cells[0])) continue;");
            sb.AppendLine("                try");
            sb.AppendLine("                {");
            sb.AppendLine($"                    {name}MetaData data = new {name}MetaData");
            sb.AppendLine("                    {");

            for (int i = 0; i < cols.Count; i++)
            {
                var    col   = cols[i];
                string parse = BuildParseExpression($"cells[{i}]", col.TypeHint, col.FieldName);
                sb.AppendLine($"                        {col.FieldName} = {parse},");
            }

            sb.AppendLine("                    };");
            sb.AppendLine($"                    _{LowerFirst(name)}Dict[data.Id] = data;");
            sb.AppendLine($"                    _{LowerFirst(name)}List.Add(data);");
            sb.AppendLine("                }");
            sb.AppendLine("                catch (Exception e)");
            sb.AppendLine("                {");
            sb.AppendLine($"                    Debug.LogWarning(\"[SpecDataManager] {name} 파싱 오류 row\" + i + \": \" + e.Message + \" | \" + string.Join(\",\", cells));");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine($"            Debug.Log(\"[SpecDataManager] {name} 로드 완료: \" + _{LowerFirst(name)}List.Count + \"개\");");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // ── Get API
        sb.AppendLine("    // ══════════════════════════════════════════════════════════");
        sb.AppendLine("    // 데이터 조회 API");
        sb.AppendLine("    // ══════════════════════════════════════════════════════════");
        foreach (var name in _results.Keys)
        {
            sb.AppendLine($"    // ── {name} ──────────────────────────────────────────");
            sb.AppendLine("    /// <summary>id로 단일 데이터 조회. 없으면 null 반환.</summary>");
            sb.AppendLine($"    public {name}MetaData Get{name}(int id)");
            sb.AppendLine("    {");
            sb.AppendLine($"        {name}MetaData result;");
            sb.AppendLine($"        _{LowerFirst(name)}Dict.TryGetValue(id, out result);");
            sb.AppendLine("        return result;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>전체 목록 반환.</summary>");
            sb.AppendLine($"    public List<{name}MetaData> GetAll{name}()");
            sb.AppendLine("    {");
            sb.AppendLine($"        return _{LowerFirst(name)}List;");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // ── 타입 변환 헬퍼
        sb.AppendLine("    // ══════════════════════════════════════════════════════════");
        sb.AppendLine("    // 타입 변환 헬퍼 (GvizParser가 숫자를 \"1.0\" 형태로 내려줄 수 있어 방어처리)");
        sb.AppendLine("    // ══════════════════════════════════════════════════════════");
        sb.AppendLine("    static int ParseInt(string s)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (string.IsNullOrEmpty(s)) return 0;");
        sb.AppendLine("        if (s.Contains(\".\"))");
        sb.AppendLine("            return (int)float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);");
        sb.AppendLine("        return int.Parse(s);");
        sb.AppendLine("    }");
        sb.AppendLine("    static float ParseFloat(string s)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (string.IsNullOrEmpty(s)) return 0f;");
        sb.AppendLine("        return float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);");
        sb.AppendLine("    }");
        sb.AppendLine("    static T ParseEnum<T>(string s) where T : struct");
        sb.AppendLine("    {");
        sb.AppendLine("        if (string.IsNullOrEmpty(s)) return default(T);");
        sb.AppendLine("        return (T)Enum.Parse(typeof(T), s, true);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("}");

        WriteFile(OUTPUT_MANAGER_PATH + "SpecDataManager.cs", sb.ToString());
    }

    // ──────────────────────────────────────────
    // CSV 파서 (에디터에서 컬럼 구조 파악 전용)
    // ──────────────────────────────────────────
    private SheetParseResult ParseStructureOnly(string sheetName, string raw)
    {
        var result  = new SheetParseResult { SheetName = sheetName };
        var allRows = SplitCsv(raw);

        if (allRows.Count < 2)
        {
            Log($"  [경고] {sheetName}: 행이 부족합니다 (최소 Row1 타입힌트 + Row2 헤더 필요)");
            return result;
        }

        var typeRow   = allRows[0]; // Row1: int/float/enum/string...
        var headerRow = allRows[1]; // Row2: 필드명

        result.Columns = new List<ColumnInfo>();
        for (int i = 0; i < headerRow.Count; i++)
        {
            string field = headerRow[i].Trim();
            if (string.IsNullOrEmpty(field)) continue;
            string hint = i < typeRow.Count ? typeRow[i].Trim().ToLower() : "string";
            result.Columns.Add(new ColumnInfo { FieldName = field, TypeHint = hint, ColIndex = i });
        }

        // enum 값 수집용 샘플 데이터
        result.SampleRows = new List<List<string>>();
        for (int r = 2; r < allRows.Count; r++)
        {
            if (allRows[r].TrueForAll(c => string.IsNullOrWhiteSpace(c))) continue;
            result.SampleRows.Add(allRows[r]);
        }

        Log($"  구조: {sheetName} → 컬럼 {result.Columns.Count}개 / 데이터 {result.SampleRows.Count}행");

        // 컬럼 목록 로그
        foreach (var col in result.Columns)
            Log($"    [{col.TypeHint}] {col.FieldName}");

        return result;
    }

    private static List<List<string>> SplitCsv(string raw)
    {
        var result = new List<List<string>>();
        string text = raw.Replace("\r\n", "\n").Replace("\r", "\n");
        int pos = 0;

        while (pos <= text.Length)
        {
            var row = new List<string>();
            while (true)
            {
                if (pos > text.Length) break;
                if (pos == text.Length || text[pos] == '\n') { pos++; break; }

                string cell;
                if (text[pos] == '"')
                {
                    pos++;
                    var cellSb = new StringBuilder();
                    while (pos < text.Length)
                    {
                        if (text[pos] == '"')
                        {
                            pos++;
                            if (pos < text.Length && text[pos] == '"') { cellSb.Append('"'); pos++; }
                            else break;
                        }
                        else cellSb.Append(text[pos++]);
                    }
                    cell = cellSb.ToString();
                    if (pos < text.Length && text[pos] == ',') pos++;
                }
                else
                {
                    int s = pos;
                    while (pos < text.Length && text[pos] != ',' && text[pos] != '\n') pos++;
                    cell = text.Substring(s, pos - s);
                    if (pos < text.Length && text[pos] == ',') pos++;
                }
                row.Add(cell);
            }
            if (row.Count > 0) result.Add(row);
        }
        return result;
    }

    // ──────────────────────────────────────────
    // 유틸
    // ──────────────────────────────────────────
    private static string HintToCsType(string hint, string fieldName) => hint switch
    {
        "int"    => "int",
        "float"  => "float",
        "double" => "double",
        "bool"   => "bool",
        "long"   => "long",
        "string" => "string",
        "enum"   => fieldName,  // enum 타입명 = 필드명
        _        => "string"
    };

    /// <summary>
    /// cells[i] 문자열을 실제 C# 타입으로 변환하는 파싱 표현식 생성
    /// </summary>
    private static string BuildParseExpression(string cellExpr, string hint, string fieldName)
    {
        switch (hint)
        {
            case "int":    return $"ParseInt({cellExpr})";
            case "float":  return $"ParseFloat({cellExpr})";
            case "double": return $"string.IsNullOrEmpty({cellExpr}) ? 0.0 : double.Parse({cellExpr}, System.Globalization.CultureInfo.InvariantCulture)";
            case "long":   return $"string.IsNullOrEmpty({cellExpr}) ? 0L : long.Parse({cellExpr})";
            case "bool":   return $"({cellExpr} == \"true\" || {cellExpr} == \"1\" || {cellExpr} == \"yes\")";
            case "string": return cellExpr;
            case "enum":   return $"ParseEnum<{fieldName}>({cellExpr})";
            default:       return cellExpr;
        }
    }

    private static string LowerFirst(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToLower(s[0]) + s.Substring(1);

    private static void AppendFileHeader(StringBuilder sb)
    {
        sb.AppendLine("// ============================================================");
        sb.AppendLine("// Auto-generated by GoogleSheetCodeGenerator.");
        sb.AppendLine("// Do NOT edit manually — 컬럼 구조 변경 시 툴을 재실행하세요.");
        sb.AppendLine("// ============================================================");
    }

    private void WriteFile(string path, string content)
    {
        File.WriteAllText(path, content, Encoding.UTF8);
        Log($"  ✓ 생성: {path}");
    }

    private static void EnsureDir(string assetPath)
    {
        string full = Path.Combine(
            Application.dataPath.Replace("Assets", ""),
            assetPath.TrimEnd('/'));
        if (!Directory.Exists(full))
            Directory.CreateDirectory(full);
    }

    private void Log(string msg)
    {
        _log += msg + "\n";
        Debug.Log("[SpecDataGen] " + msg);
        Repaint();
    }
}

// ──────────────────────────────────────────
// 에디터 내부 데이터 구조
// ──────────────────────────────────────────
public class SheetParseResult
{
    public string             SheetName;
    public List<ColumnInfo>   Columns;
    public List<List<string>> SampleRows; // enum 값 수집 + 검증용 샘플
}

public class ColumnInfo
{
    public string FieldName;
    public string TypeHint;
    public int    ColIndex;
}

public struct SheetInfo
{
    public string sheetName;
    public string gid;
}

#endif