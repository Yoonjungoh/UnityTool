using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class UI_Test : UI_Scene
{
    // UI 자동화 사용 예제 컴포넌트
    Button _testButton;
    TextMeshProUGUI _testText;
    int _score = 0;
    enum Buttons
    {
        TestButton,
    }
    enum Texts
    {
        TestText,
    }
    void Start()
    {
        Init();
    }
    public override void Init()
    {
        base.Init();
        Bind<Button>(typeof(Buttons));
        Bind<TextMeshProUGUI>(typeof(Texts));
        _testButton = GetButton((int)Buttons.TestButton);
        _testText = GetTextMeshProUGUI((int)Texts.TestText);
        _testButton.onClick.AddListener(AddScore);
        _testText.text = _score.ToString();
    }
    void AddScore()
    {
        _score++;
        _testText.text = _score.ToString();
        Managers.Scene.LoadScene("Login");
    }
}
