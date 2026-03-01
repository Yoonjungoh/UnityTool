#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// [에디터 전용] Google Sheet 구조를 읽어 런타임 코드를 자동 생성합니다.
///
/// ★ 워크플로우
///   1. Spreadsheet ID + API Key 입력
///   2. [시트 자동 탐색] → Google Sheets API v4로 시트 목록 안정적 파싱
///   3. [코드 자동 생성] → 각 시트 GViz API 구조 읽어 코드 생성
///
///   새 시트 추가 시: 구글 시트에 추가 → [시트 자동 탐색] → [코드 자동 생성]
///
/// ★ API Key 발급 (무료, 1회 설정)
///   1. https://console.cloud.google.com → 프로젝트 생성
///   2. [API 및 서비스] → [라이브러리] → "Google Sheets API" 검색 → 사용 설정
///   3. [API 및 서비스] → [사용자 인증 정보] → [+ 사용자 인증 정보 만들기] → API 키
///   4. 생성된 키를 아래 API Key 란에 붙여넣기
///
/// ★ 생성 파일 목록
///   SheetEnums.cs          → enum 정의
///   MetaData.cs            → 모든 XxxMetaData 클래스
///   XxxMetaDataSO.cs       → ScriptableObject 구조 정의
///   GoogleSheetConfig.cs   → SpreadsheetId / URL 빌더 (시트 이름 기반)
///   SpecDataManager.cs     → 런타임 다운로드 + 파싱 + Get API
/// </summary>
public class GoogleSheetCodeGenerator : EditorWindow
{
    // ★ 출력 경로 (프로젝트 구조에 맞게 수정)
    private const string OUTPUT_DATA_PATH    = "Assets/Scripts/Data/";
    private const string OUTPUT_SO_PATH      = "Assets/Scripts/Data/SO/";
    private const string OUTPUT_MANAGER_PATH = "Assets/Scripts/Managers/Core/";

    // ── 설정값 (EditorPrefs로 세션 간 유지)
    private string          _spreadsheetId = "";
    private string          _apiKey        = "";
    private List<SheetInfo> _sheets        = new List<SheetInfo>();

    // ── 파이프라인 상태
    private enum State { Idle, Discovering, FetchingStructure }
    private State _state = State.Idle;
    private bool IsIdle => _state == State.Idle;

    // ── 코드 생성 파이프라인 내부 상태
    private readonly Dictionary<string, SheetParseResult> _results = new();
    private Queue<SheetInfo> _queue;
    private UnityWebRequest  _req;
    private SheetInfo        _cur;

    // ── UI 상태
    private string  _log = "Spreadsheet ID와 API Key를 입력하고 [시트 자동 탐색]을 눌러 시작하세요.\n\n" +
                           "API Key 발급: console.cloud.google.com → Google Sheets API 활성화 → API 키 생성\n" +
                           "스프레드시트 공개 설정: 링크가 있는 모든 사용자 - 뷰어";
    private Vector2 _scroll;
    private string  _newSheetName  = "";
    private bool    _showApiKey    = false;

    // ── EditorPrefs 키
    private const string PREF_ID      = "GViz_SpreadsheetId";
    private const string PREF_SHEETS  = "GViz_Sheets";
    private const string PREF_API_KEY = "GViz_ApiKey";

    [MenuItem("Tools/Spec Data Generator")]
    public static void Open() => GetWindow<GoogleSheetCodeGenerator>("Spec Data Generator").Show();

    private void OnEnable()  => LoadPrefs();
    private void OnDisable() => SavePrefs();

    // ──────────────────────────────────────────
    // EditorPrefs
    // ──────────────────────────────────────────
    private void SavePrefs()
    {
        EditorPrefs.SetString(PREF_ID,      _spreadsheetId);
        EditorPrefs.SetString(PREF_API_KEY, _apiKey);
        var parts = new List<string>();
        foreach (var s in _sheets) parts.Add(s.sheetName);
        EditorPrefs.SetString(PREF_SHEETS, string.Join(";", parts));
    }

    private void LoadPrefs()
    {
        _spreadsheetId = EditorPrefs.GetString(PREF_ID,      "");
        _apiKey        = EditorPrefs.GetString(PREF_API_KEY, "");
        _sheets.Clear();
        string raw = EditorPrefs.GetString(PREF_SHEETS, "");
        if (!string.IsNullOrEmpty(raw))
        {
            foreach (var part in raw.Split(';'))
            {
                // 구버전 "name|gid" 포맷 호환
                int pipeIdx = part.IndexOf('|');
                string name = pipeIdx >= 0 ? part.Substring(0, pipeIdx) : part.Trim();
                if (!string.IsNullOrEmpty(name))
                    _sheets.Add(new SheetInfo { sheetName = name });
            }
        }
    }

    // ──────────────────────────────────────────
    // GUI
    // ──────────────────────────────────────────
    private void OnGUI()
    {
        GUILayout.Label("Spec Data 코드 자동 생성기", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);

        EditorGUI.BeginDisabledGroup(!IsIdle);

        // ── Spreadsheet ID
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("ID / URL", GUILayout.Width(115));
        string newId = ExtractSpreadsheetId(EditorGUILayout.TextField(_spreadsheetId));
        if (newId != _spreadsheetId) { _spreadsheetId = newId; SavePrefs(); }
        EditorGUILayout.EndHorizontal();

        // ── API Key
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("API Key", GUILayout.Width(115));
        string newKey = _showApiKey
            ? EditorGUILayout.TextField(_apiKey)
            : EditorGUILayout.PasswordField(_apiKey);
        if (newKey != _apiKey) { _apiKey = newKey; SavePrefs(); }
        if (GUILayout.Button(_showApiKey ? "숨김" : "표시", GUILayout.Width(40)))
            _showApiKey = !_showApiKey;
        EditorGUILayout.EndHorizontal();

        // API Key 상태 표시
        if (string.IsNullOrEmpty(_apiKey.Trim()))
        {
            EditorGUILayout.HelpBox(
                "API Key를 입력하면 시트 자동 탐색이 안정적으로 동작합니다.\n" +
                "발급: console.cloud.google.com → Google Sheets API → API 키 생성",
                MessageType.Warning);
        }
        else
        {
            EditorGUILayout.HelpBox("API Key 설정됨 ✓", MessageType.Info);
        }

        EditorGUI.EndDisabledGroup();

        EditorGUILayout.Space(6);

        // ── 시트 목록
        GUILayout.Label("시트 목록", EditorStyles.boldLabel);

        bool canDiscover = IsIdle && !string.IsNullOrEmpty(_spreadsheetId) && !string.IsNullOrEmpty(_apiKey.Trim());
        EditorGUI.BeginDisabledGroup(!canDiscover);
        GUI.backgroundColor = new Color(0.4f, 0.7f, 1f);
        if (GUILayout.Button("🔍  시트 자동 탐색"))
            StartDiscovery();
        GUI.backgroundColor = Color.white;
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.Space(4);

        // 시트 리스트
        EditorGUI.BeginDisabledGroup(!IsIdle);
        int removeIdx = -1;
        for (int i = 0; i < _sheets.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            string newName = EditorGUILayout.TextField(_sheets[i].sheetName);
            if (newName != _sheets[i].sheetName)
            {
                _sheets[i] = new SheetInfo { sheetName = newName };
                SavePrefs();
            }
            if (GUILayout.Button("✕", GUILayout.Width(24))) removeIdx = i;
            EditorGUILayout.EndHorizontal();
        }
        if (removeIdx >= 0) { _sheets.RemoveAt(removeIdx); SavePrefs(); }

        // 수동 추가
        EditorGUILayout.BeginHorizontal();
        _newSheetName = EditorGUILayout.TextField(_newSheetName);
        if (GUILayout.Button("+ 추가", GUILayout.Width(60))
            && !string.IsNullOrWhiteSpace(_newSheetName))
        {
            _sheets.Add(new SheetInfo { sheetName = _newSheetName.Trim() });
            _newSheetName = "";
            SavePrefs();
        }
        EditorGUILayout.EndHorizontal();
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.Space(8);

        // ── 코드 생성
        EditorGUI.BeginDisabledGroup(!IsIdle || _sheets.Count == 0);
        GUI.backgroundColor = new Color(0.3f, 0.85f, 0.45f);
        if (GUILayout.Button("▶  코드 자동 생성", GUILayout.Height(44)))
            StartCodeGen();
        GUI.backgroundColor = Color.white;
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.Space(8);
        GUILayout.Label("로그", EditorStyles.boldLabel);
        _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
        EditorGUILayout.TextArea(_log, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

    // ──────────────────────────────────────────
    // 시트 자동 탐색 (Google Sheets API v4)
    // ──────────────────────────────────────────
    private void StartDiscovery()
    {
        _state = State.Discovering;
        _log = "";
        Log("=== 시트 자동 탐색 ===");
        Log("Google Sheets API v4 요청 중...");

        // fields=sheets.properties → 시트 목록만 가져오기 (데이터 제외)
        string url = $"https://sheets.googleapis.com/v4/spreadsheets/{_spreadsheetId}" +
                     $"?fields=sheets.properties&key={_apiKey.Trim()}";
        _req = UnityWebRequest.Get(url);
        _req.SendWebRequest();
        EditorApplication.update += TickDiscovery;
    }

    private void TickDiscovery()
    {
        if (!_req.isDone) return;
        EditorApplication.update -= TickDiscovery;

        string responseText = _req.downloadHandler.text;

        if (_req.result == UnityWebRequest.Result.Success)
        {
            var discovered = ParseSheetsFromV4Json(responseText);
            if (discovered.Count > 0)
            {
                _sheets = discovered;
                SavePrefs();
                Log($"✅ {discovered.Count}개 시트 발견:");
                foreach (var s in _sheets) Log($"  • {s.sheetName}");
            }
            else
            {
                Log("[경고] 시트 목록 파싱 실패. 응답:");
                Log(responseText.Substring(0, Math.Min(400, responseText.Length)));
            }
        }
        else
        {
            Log($"[오류] 요청 실패: {_req.responseCode} {_req.error}");

            // 구체적인 오류 메시지 파싱
            if (responseText.Contains("API_KEY_INVALID") || responseText.Contains("API key not valid"))
                Log("→ API Key가 유효하지 않습니다. Google Cloud Console에서 키를 확인하세요.");
            else if (responseText.Contains("PERMISSION_DENIED") || responseText.Contains("403"))
                Log("→ 스프레드시트가 공개되어 있는지 확인하세요: 공유 → 링크가 있는 모든 사용자 → 뷰어");
            else if (responseText.Contains("NOT_FOUND") || responseText.Contains("404"))
                Log("→ Spreadsheet ID를 확인하세요.");
            else if (responseText.Contains("Sheets API has not been used") || responseText.Contains("SERVICE_DISABLED"))
                Log("→ Google Cloud Console에서 'Google Sheets API'를 활성화하세요.");
            else
                Log("→ 응답:\n" + responseText.Substring(0, Math.Min(400, responseText.Length)));
        }

        _req.Dispose();
        _req = null;
        _state = State.Idle;
        Repaint();
    }

    /// <summary>
    /// Google Sheets API v4 응답에서 시트 이름 목록을 파싱합니다.
    /// 응답 형식: {"sheets":[{"properties":{"sheetId":0,"title":"SheetName",...}},...]
    /// </summary>
    private static List<SheetInfo> ParseSheetsFromV4Json(string json)
    {
        var result = new List<SheetInfo>();
        if (string.IsNullOrEmpty(json)) return result;

        // "sheets" 배열 위치 찾기
        int sheetsKey = json.IndexOf("\"sheets\"");
        if (sheetsKey < 0) return result;

        int arrayStart = json.IndexOf('[', sheetsKey);
        if (arrayStart < 0) return result;

        int arrayEnd = FindMatchingBracket(json, arrayStart);
        if (arrayEnd < 0) return result;

        string sheetsArray = json.Substring(arrayStart, arrayEnd - arrayStart + 1);

        // 각 properties 블록에서 "title":"SheetName" 추출
        // fields=sheets.properties 파라미터 덕분에 응답에는 시트 타이틀만 있음
        var regex = new Regex("\"title\"\\s*:\\s*\"([^\"]+)\"");
        foreach (Match m in regex.Matches(sheetsArray))
        {
            string name = m.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(name))
                result.Add(new SheetInfo { sheetName = name });
        }

        return result;
    }

    // ──────────────────────────────────────────
    // 코드 생성 파이프라인
    // ──────────────────────────────────────────
    private void StartCodeGen()
    {
        _state = State.FetchingStructure;
        _log = "";
        _results.Clear();
        Log("=== 시트 구조 분석 시작 ===");
        _queue = new Queue<SheetInfo>(_sheets);
        EditorApplication.update += TickCodeGen;
    }

    private void TickCodeGen()
    {
        if (_req != null)
        {
            if (!_req.isDone) return;

            if (_req.result == UnityWebRequest.Result.Success)
            {
                Log($"  ✓ 구조 파싱 완료: {_cur.sheetName}");
                _results[_cur.sheetName] = ParseStructureFromGviz(_cur.sheetName, _req.downloadHandler.text);
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
            // gviz API: 시트 이름으로 직접 조회 (GID 불필요)
            string url = $"https://docs.google.com/spreadsheets/d/{_spreadsheetId}" +
                         $"/gviz/tq?tqx=out:json&sheet={Uri.EscapeDataString(_cur.sheetName)}";
            Log($"구조 분석 중: {_cur.sheetName} ...");
            _req = UnityWebRequest.Get(url);
            _req.SendWebRequest();
            return;
        }

        EditorApplication.update -= TickCodeGen;
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

        _state = State.Idle;
        Log("\n✅ 코드 생성 완료!");
        Log("");
        Log("[ 런타임 흐름 ]");
        Log("  Managers.Start()");
        Log("    → CoSpecDataManagerInit()");
        Log("      → SpecData.CoDownloadDataSheet()");
        foreach (var name in _results.Keys)
            Log($"        → CoFetch_{name}()");
        Log("      → IsReady = true");
        Log("");
        Log("[ 사용 예시 ]");
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
    // 3. XxxMetaDataSO.cs
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
        sb.AppendLine("/// GID 대신 시트 이름 기반으로 URL을 구성합니다.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class GoogleSheetConfig");
        sb.AppendLine("{");
        sb.AppendLine($"    public const string SpreadsheetId = \"{_spreadsheetId}\";");
        sb.AppendLine();
        sb.AppendLine("    public static readonly List<string> SheetNames =");
        sb.AppendLine("        new List<string>");
        sb.AppendLine("    {");
        foreach (var sheet in _sheets)
            sb.AppendLine($"        \"{sheet.sheetName}\",");
        sb.AppendLine("    };");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Google Visualization JSON API URL (공개 시트, API Key 불필요)");
        sb.AppendLine("    /// 시트 이름으로 조회 — GID 불필요.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public static string BuildJsonUrl(string sheetName)");
        sb.AppendLine("    {");
        sb.AppendLine("        return string.Format(");
        sb.AppendLine("            \"https://docs.google.com/spreadsheets/d/{0}/gviz/tq?tqx=out:json&sheet={1}\",");
        sb.AppendLine("            SpreadsheetId, System.Uri.EscapeDataString(sheetName));");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        WriteFile(OUTPUT_DATA_PATH + "GoogleSheetConfig.cs", sb.ToString());
    }

    // ──────────────────────────────────────────
    // 5. SpecDataManager.cs
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
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public class SpecDataManager");
        sb.AppendLine("{");

        sb.AppendLine("    public bool IsReady { get; private set; }");
        sb.AppendLine();

        foreach (var name in _results.Keys)
        {
            sb.AppendLine($"    Dictionary<int, {name}MetaData> _{LowerFirst(name)}Dict = new Dictionary<int, {name}MetaData>();");
            sb.AppendLine($"    List<{name}MetaData>            _{LowerFirst(name)}List = new List<{name}MetaData>();");
        }
        sb.AppendLine();

        sb.AppendLine("    public IEnumerator CoDownloadDataSheet()");
        sb.AppendLine("    {");
        sb.AppendLine("        IsReady = false;");
        sb.AppendLine("        Debug.Log(\"[SpecDataManager] 데이터 다운로드 시작\");");
        sb.AppendLine();
        foreach (var name in _results.Keys)
            sb.AppendLine($"        yield return CoFetch_{name}();");
        sb.AppendLine();
        sb.AppendLine("        IsReady = true;");
        sb.AppendLine("        Debug.Log(\"[SpecDataManager] 모든 데이터 로드 완료\");");
        sb.AppendLine("    }");
        sb.AppendLine();

        foreach (var kv in _results)
        {
            string name = kv.Value.SheetName;
            var    cols = kv.Value.Columns;

            sb.AppendLine($"    IEnumerator CoFetch_{name}()");
            sb.AppendLine("    {");
            sb.AppendLine($"        string url = GoogleSheetConfig.BuildJsonUrl(\"{name}\");");
            sb.AppendLine("        using (UnityWebRequest req = UnityWebRequest.Get(url))");
            sb.AppendLine("        {");
            sb.AppendLine("            yield return req.SendWebRequest();");
            sb.AppendLine("            if (req.result != UnityWebRequest.Result.Success)");
            sb.AppendLine("            {");
            sb.AppendLine($"                Debug.LogError(\"[SpecDataManager] {name} 다운로드 실패: \" + req.error);");
            sb.AppendLine("                yield break;");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine($"            _{LowerFirst(name)}Dict.Clear();");
            sb.AppendLine($"            _{LowerFirst(name)}List.Clear();");
            sb.AppendLine();
            sb.AppendLine($"            List<string[]> rows = GvizParser.Parse(req.downloadHandler.text, colCount: {cols.Count});");
            sb.AppendLine("            for (int i = 2; i < rows.Count; i++)");
            sb.AppendLine("            {");
            sb.AppendLine("                string[] cells = rows[i];");
            sb.AppendLine("                if (string.IsNullOrEmpty(cells[0])) continue;");
            sb.AppendLine("                try");
            sb.AppendLine("                {");
            sb.AppendLine($"                    {name}MetaData data = new {name}MetaData");
            sb.AppendLine("                    {");
            for (int i = 0; i < cols.Count; i++)
            {
                var col   = cols[i];
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

        foreach (var name in _results.Keys)
        {
            sb.AppendLine($"    public {name}MetaData Get{name}(int id)");
            sb.AppendLine("    {");
            sb.AppendLine($"        {name}MetaData result;");
            sb.AppendLine($"        _{LowerFirst(name)}Dict.TryGetValue(id, out result);");
            sb.AppendLine("        return result;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine($"    public List<{name}MetaData> GetAll{name}()");
            sb.AppendLine("    {");
            sb.AppendLine($"        return _{LowerFirst(name)}List;");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

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
        sb.AppendLine("}");

        WriteFile(OUTPUT_MANAGER_PATH + "SpecDataManager.cs", sb.ToString());
    }

    // ──────────────────────────────────────────
    // GViz JSON → 시트 구조 파싱
    // ──────────────────────────────────────────
    private SheetParseResult ParseStructureFromGviz(string sheetName, string raw)
    {
        var result = new SheetParseResult { SheetName = sheetName };

        List<string[]> rows = GvizParser.Parse(raw);

        if (rows.Count < 2)
        {
            Log($"  [경고] {sheetName}: 데이터 행 부족 (Row1 타입힌트 + Row2 헤더 필요)");
            result.Columns    = new List<ColumnInfo>();
            result.SampleRows = new List<List<string>>();
            return result;
        }

        string[] typeRow   = rows[0];
        string[] headerRow = rows[1];

        result.Columns = new List<ColumnInfo>();
        for (int i = 0; i < headerRow.Length; i++)
        {
            string field = headerRow[i].Trim();
            if (string.IsNullOrEmpty(field)) continue;
            string hint = i < typeRow.Length ? typeRow[i].Trim().ToLower() : "string";
            result.Columns.Add(new ColumnInfo { FieldName = field, TypeHint = hint, ColIndex = i });
        }

        result.SampleRows = new List<List<string>>();
        for (int r = 2; r < rows.Count; r++)
        {
            string[] row = rows[r];
            bool allEmpty = true;
            foreach (var c in row) if (!string.IsNullOrWhiteSpace(c)) { allEmpty = false; break; }
            if (allEmpty) continue;
            result.SampleRows.Add(new List<string>(row));
        }

        Log($"  구조: {sheetName} → 컬럼 {result.Columns.Count}개 / 데이터 {result.SampleRows.Count}행");
        foreach (var col in result.Columns)
            Log($"    [{col.TypeHint}] {col.FieldName}");

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
        "enum"   => fieldName,
        _        => "string"
    };

    private static string BuildParseExpression(string cellExpr, string hint, string fieldName)
    {
        return hint switch
        {
            "int"    => $"ParseInt({cellExpr})",
            "float"  => $"ParseFloat({cellExpr})",
            "double" => $"string.IsNullOrEmpty({cellExpr}) ? 0.0 : double.Parse({cellExpr}, System.Globalization.CultureInfo.InvariantCulture)",
            "long"   => $"string.IsNullOrEmpty({cellExpr}) ? 0L : long.Parse({cellExpr})",
            "bool"   => $"({cellExpr} == \"true\" || {cellExpr} == \"1\" || {cellExpr} == \"yes\")",
            "string" => cellExpr,
            "enum"   => $"ParseEnum<{fieldName}>({cellExpr})",
            _        => cellExpr,
        };
    }

    private static string LowerFirst(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToLower(s[0]) + s.Substring(1);

    private static string ExtractSpreadsheetId(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var m = Regex.Match(input, @"spreadsheets/d/([a-zA-Z0-9_-]+)");
        return m.Success ? m.Groups[1].Value : input.Trim();
    }

    private static int FindMatchingBracket(string s, int open)
    {
        int depth = 0;
        bool inStr = false;
        for (int i = open; i < s.Length; i++)
        {
            if (inStr) { if (s[i] == '\\') i++; else if (s[i] == '"') inStr = false; continue; }
            if (s[i] == '"') { inStr = true; continue; }
            if (s[i] == '[') depth++;
            else if (s[i] == ']') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

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
    public List<List<string>> SampleRows;
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
}

#endif
