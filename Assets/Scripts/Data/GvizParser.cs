using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Google Visualization JSON API 응답 파서.
///
/// gviz 응답 형식 (parsedNumHeaders:2 기준):
///   "cols":[{"id":"A","label":"int Id","type":"number"}, ...]
///   "rows":[
///     {"c":[{"v":1.0,"f":"1"},{"v":"Gold"},null,null,...]},   ← 순수 데이터 행
///     ...
///   ]
///
/// 핵심 주의사항:
///   - gviz는 parsedNumHeaders:N 일 때 시트의 상위 N개 행(타입힌트+헤더)을
///     cols[i].label에 합쳐서 내려줌 ("int Id" 형태).
///     즉 rows[] 배열은 데이터 행만 담고 있음 — 헤더 행이 없음!
///   - 이 파서는 cols[i].label을 분리해 rows[0]/rows[1]을 합성하여
///     항상 rows[0]=타입힌트, rows[1]=헤더, rows[2~]=데이터 계약을 유지함.
///   - null 셀: {"v":null} 또는 리터럴 null 둘 다 빈 문자열로 처리
///   - 숫자는 float로 내려옴 (1.0 → "1", 50.0 → "50")
///   - 뒤쪽 생략된 셀은 colCount만큼 패딩
///
/// 반환: List&lt;string[]&gt;
///   [0] = 타입힌트 행 (int, float, enum, ...)
///   [1] = 헤더 행    (Id, MonsterType, ...)
///   [2~] = 데이터 행
/// </summary>
public static class GvizParser
{
    /// <summary>
    /// gviz JSON 응답을 파싱하여 rows[i][j] = 셀 문자열 형태로 반환.
    /// colCount를 지정하면 짧은 행을 빈 문자열로 패딩합니다.
    /// </summary>
    public static List<string[]> Parse(string raw, int colCount = 0)
    {
        var result = new List<string[]>();

        if (string.IsNullOrEmpty(raw))
        {
            Debug.LogError("[GvizParser] 응답이 비어있습니다.");
            return result;
        }

        // ── Step 1: JSONP 래퍼 제거 ─────────────────────────────
        // "/*O_o*/\ngoogle.visualization.Query.setResponse({...});"
        int jsonStart = raw.IndexOf('{');
        int jsonEnd   = raw.LastIndexOf('}');
        if (jsonStart < 0 || jsonEnd < 0 || jsonEnd <= jsonStart)
        {
            Debug.LogError("[GvizParser] JSON 영역 없음. RAW 앞부분:\n"
                + raw.Substring(0, Mathf.Min(300, raw.Length)));
            return result;
        }
        string json = raw.Substring(jsonStart, jsonEnd - jsonStart + 1);

        // ── Step 2: cols 레이블 추출 → rows[0]/rows[1] 합성 ────
        // gviz는 parsedNumHeaders:2 일 때 시트의 두 헤더 행을
        // cols[i].label = "타입 헤더명" 형태로 합쳐서 내려줌.
        // rows[] 배열에는 데이터 행만 있으므로 여기서 직접 합성.
        List<string> colLabels = ExtractColLabels(json);

        if (colCount == 0)
            colCount = colLabels.Count > 0 ? colLabels.Count : CountCols(json);

        string[] typeRow   = new string[colCount];
        string[] headerRow = new string[colCount];
        for (int i = 0; i < colCount; i++)
        {
            string label = i < colLabels.Count ? colLabels[i] : "";
            int spaceIdx = label.IndexOf(' ');
            if (spaceIdx > 0)
            {
                typeRow[i]   = label.Substring(0, spaceIdx);
                headerRow[i] = label.Substring(spaceIdx + 1);
            }
            else
            {
                typeRow[i]   = "";
                headerRow[i] = label;
            }
        }
        result.Add(typeRow);   // rows[0] = 타입힌트
        result.Add(headerRow); // rows[1] = 헤더

        // ── Step 3: "rows" 배열 찾기 ────────────────────────────
        int rowsIdx = json.IndexOf("\"rows\"");
        if (rowsIdx < 0)
        {
            Debug.LogError("[GvizParser] \"rows\" 키 없음.");
            return result;
        }
        int rowsArrayStart = json.IndexOf('[', rowsIdx);
        if (rowsArrayStart < 0) return result;

        // ── Step 4: 각 행 파싱 (전부 데이터 행) ─────────────────
        int pos = rowsArrayStart + 1;

        while (pos < json.Length)
        {
            // 다음 행 블록 {"c": 찾기
            int cStart = FindNext(json, "{\"c\"", pos);
            if (cStart < 0) break;

            int cellArrayStart = json.IndexOf('[', cStart + 4);
            if (cellArrayStart < 0) break;

            int cellArrayEnd = FindMatchingBracket(json, cellArrayStart);
            if (cellArrayEnd < 0) break;

            string cellsJson = json.Substring(
                cellArrayStart + 1,
                cellArrayEnd - cellArrayStart - 1);

            string[] row = ParseCellArray(cellsJson, colCount);
            result.Add(row); // rows[2~] = 데이터

            pos = cellArrayEnd + 1;
        }

        return result;
    }

    // ── cols[i].label 목록 추출 ──────────────────────────────────
    private static List<string> ExtractColLabels(string json)
    {
        var labels = new List<string>();
        int colsIdx = json.IndexOf("\"cols\"");
        if (colsIdx < 0) return labels;

        int colsStart = json.IndexOf('[', colsIdx);
        if (colsStart < 0) return labels;

        int colsEnd = FindMatchingBracket(json, colsStart);
        if (colsEnd < 0) return labels;

        int pos = colsStart + 1;
        while (pos < colsEnd)
        {
            int objStart = json.IndexOf('{', pos);
            if (objStart < 0 || objStart >= colsEnd) break;

            int objEnd = FindMatchingBrace(json, objStart);
            if (objEnd < 0 || objEnd > colsEnd) break;

            string colObj = json.Substring(objStart, objEnd - objStart + 1);
            labels.Add(ExtractStringField(colObj, "label"));

            pos = objEnd + 1;
        }
        return labels;
    }

    // ── JSON 오브젝트에서 문자열 필드 값 추출 ───────────────────
    // {"label":"int Id","type":"number"} + "label" → "int Id"
    private static string ExtractStringField(string obj, string fieldName)
    {
        string key = "\"" + fieldName + "\"";
        int idx = obj.IndexOf(key);
        if (idx < 0) return "";

        int colon = obj.IndexOf(':', idx + key.Length);
        if (colon < 0) return "";

        int vs = colon + 1;
        while (vs < obj.Length && obj[vs] == ' ') vs++;
        if (vs >= obj.Length || obj[vs] != '"') return "";

        int end = vs + 1;
        while (end < obj.Length)
        {
            if (obj[end] == '\\') { end += 2; continue; }
            if (obj[end] == '"') break;
            end++;
        }
        string s = obj.Substring(vs + 1, end - vs - 1);
        return s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n");
    }

    // ── cols 수 파악 (레이블 추출 실패 시 폴백) ──────────────────
    private static int CountCols(string json)
    {
        int colsIdx = json.IndexOf("\"cols\"");
        if (colsIdx < 0) return 0;

        int colsStart = json.IndexOf('[', colsIdx);
        if (colsStart < 0) return 0;

        int colsEnd = FindMatchingBracket(json, colsStart);
        if (colsEnd < 0) return 0;

        int count = 0;
        int depth = 0;
        bool inStr = false;
        for (int i = colsStart; i <= colsEnd; i++)
        {
            char c = json[i];
            if (inStr) { if (c == '\\') i++; else if (c == '"') inStr = false; continue; }
            if (c == '"') { inStr = true; continue; }
            if (c == '{') { depth++; if (depth == 1) count++; }
            else if (c == '}') depth--;
        }
        return count;
    }

    // ── 셀 배열 파싱 → colCount 길이의 string[] ─────────────────
    // null 리터럴 셀은 위치를 추적하여 빈 문자열로 처리
    private static string[] ParseCellArray(string cellsJson, int colCount)
    {
        var cells = new List<string>();
        int pos = 0;
        int len = cellsJson.Length;

        while (pos < len)
        {
            // 공백·쉼표 스킵
            while (pos < len && (cellsJson[pos] == ',' || cellsJson[pos] == ' '
                || cellsJson[pos] == '\r' || cellsJson[pos] == '\n'
                || cellsJson[pos] == '\t')) pos++;
            if (pos >= len) break;

            if (cellsJson[pos] == '{')
            {
                // 셀 오브젝트
                int cellEnd = FindMatchingBrace(cellsJson, pos);
                if (cellEnd < 0) break;

                string cellJson = cellsJson.Substring(pos, cellEnd - pos + 1);
                cells.Add(ExtractV(cellJson));
                pos = cellEnd + 1;
            }
            else if (pos + 3 < len && cellsJson.Substring(pos, 4) == "null")
            {
                // 리터럴 null → 빈 문자열로 기록 (위치 유지)
                cells.Add(string.Empty);
                pos += 4;
            }
            else
            {
                // 예상치 못한 문자 → 스킵
                pos++;
            }
        }

        // colCount만큼 빈 문자열로 패딩 (뒤쪽 생략된 셀 보완)
        while (cells.Count < colCount)
            cells.Add(string.Empty);

        return cells.ToArray();
    }

    // ── 셀 오브젝트에서 "v" 값만 추출 ───────────────────────────
    // {"v":1.0,"f":"1"}  → "1"
    // {"v":3.14}         → "3.14"
    // {"v":"Bear"}       → "Bear"
    // {"v":null}         → ""
    private static string ExtractV(string cell)
    {
        int vIdx = cell.IndexOf("\"v\"");
        if (vIdx < 0) return string.Empty;

        int colon = cell.IndexOf(':', vIdx + 3);
        if (colon < 0) return string.Empty;

        int vs = colon + 1;
        while (vs < cell.Length && cell[vs] == ' ') vs++;
        if (vs >= cell.Length) return string.Empty;

        char first = cell[vs];

        // null
        if (vs + 3 < cell.Length && cell.Substring(vs, 4) == "null")
            return string.Empty;

        // 문자열
        if (first == '"')
        {
            int end = vs + 1;
            while (end < cell.Length)
            {
                if (cell[end] == '\\') { end += 2; continue; }
                if (cell[end] == '"') break;
                end++;
            }
            string s = cell.Substring(vs + 1, end - vs - 1);
            s = s.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n");
            return s;
        }

        // 숫자
        if (char.IsDigit(first) || first == '-')
        {
            int end = vs;
            while (end < cell.Length
                && (char.IsDigit(cell[end]) || cell[end] == '.'
                    || cell[end] == '-' || cell[end] == 'E'
                    || cell[end] == 'e' || cell[end] == '+'))
                end++;

            string num = cell.Substring(vs, end - vs);

            if (float.TryParse(num,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out float f))
            {
                if (f == System.Math.Floor(f) && !float.IsInfinity(f))
                    return ((long)f).ToString();
                return f.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return num;
        }

        return string.Empty;
    }

    // ── 다음 문자열 출현 위치 ────────────────────────────────────
    private static int FindNext(string s, string target, int from)
    {
        return s.IndexOf(target, from, System.StringComparison.Ordinal);
    }

    // ── '[' 대응 ']' 찾기 ────────────────────────────────────────
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

    // ── '{' 대응 '}' 찾기 ────────────────────────────────────────
    private static int FindMatchingBrace(string s, int open)
    {
        int depth = 0;
        bool inStr = false;
        for (int i = open; i < s.Length; i++)
        {
            if (inStr) { if (s[i] == '\\') i++; else if (s[i] == '"') inStr = false; continue; }
            if (s[i] == '"') { inStr = true; continue; }
            if (s[i] == '{') depth++;
            else if (s[i] == '}') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }
}
