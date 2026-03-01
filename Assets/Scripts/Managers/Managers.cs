using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Managers : MonoBehaviour
{
    static Managers s_instance;
    static Managers Instance { get { Init(); return s_instance; } }

    DataManager _data = new DataManager();
    ResourceManager _resource = new ResourceManager();
    SceneManagerEx _scene = new SceneManagerEx();
    SoundManager _sound = new SoundManager();
    SpecDataManager _specData = new SpecDataManager();
    UIManager _ui = new UIManager();
    URLManager _url = new URLManager();

    public static DataManager Data { get { return Instance._data; } }
    public static ResourceManager Resource { get { return Instance._resource; } }
    public static SceneManagerEx Scene { get { return Instance._scene; } }
    public static SoundManager Sound { get { return Instance._sound; } }
    public static SpecDataManager SpecData { get { return Instance._specData; } }
    public static UIManager UI { get { return Instance._ui; } }
    public static URLManager URL { get { return Instance._url; } }

    void Start()
    {
        Init();
        StartCoroutine(CoSpecDataManagerInit());
    }

    static void Init()
    {
        if (s_instance == null)
        {
            GameObject go = GameObject.Find("@Managers");
            if (go == null)
            {
                go = new GameObject { name = "@Managers" };
                go.AddComponent<Managers>();
            }

            DontDestroyOnLoad(go);
            s_instance = go.GetComponent<Managers>();

            s_instance._sound.Init();
            s_instance._resource.Init();
        }
    }

    public static void Clear()
    {
        Sound.Clear();
        UI.Clear();
    }

    // ══════════════════════════════════════════════════════════
    // SpecData 초기화 코루틴
    // ══════════════════════════════════════════════════════════
    public IEnumerator CoSpecDataManagerInit()
    {
        // CoDownloadDataSheet()를 yield return으로 완료까지 대기
        // → 이 코루틴이 끝난 시점에 SpecData.IsReady == true 보장
        yield return StartCoroutine(SpecData.CoDownloadDataSheet());

        // SpecData 로드 완료 후 처리
        OnSpecDataReady();
    }

    void OnSpecDataReady()
    {
        Debug.Log("[Managers] SpecData 준비 완료. 게임 시작 가능.");

        // 예시: 첫 씬 로드
        // Scene.LoadScene(Define.Scene.Game);

        // 예시: 로딩 UI 종료
        // UI.ClosePopupUI<UI_Loading>();
    }
}