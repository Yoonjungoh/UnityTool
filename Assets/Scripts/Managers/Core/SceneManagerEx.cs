﻿using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneManagerEx
{
    public Define.Scene _currentScene;
    public Define.Scene CurrentScene
    {
        get { return _currentScene; }
        set
        {
            if (_currentScene == value)
                return;
            _currentScene = value;
            Managers.Sound.ChangeBGM();
        }
    }
    public void LoadScene(Define.Scene type)
    {
        Managers.Clear();

        SceneManager.LoadScene(GetSceneName(type));
    }
    public void LoadScene(string sceneName)
    {
        Define.Scene sceneEnumValue = Define.Scene.Unknown;

        if (Enum.TryParse(sceneName, out sceneEnumValue))
        {
            switch (sceneEnumValue)
            {
                case Define.Scene.Login:
                    break;
                case Define.Scene.Lobby:
                    break;
                case Define.Scene.Game:
                    break;
                case Define.Scene.StartMenu:
                    break;
                default:
                    break;
            }
            UI_LoadingScene.Instance.LoadScene(sceneName);
            Debug.Log($"Load {sceneName} Scene");
        }
        else
        {
            Debug.Log($"Dont exist {sceneName} Scene");
        }
        CurrentScene = sceneEnumValue;
    }
    string GetSceneName(Define.Scene type)
    {
        string name = System.Enum.GetName(typeof(Define.Scene), type);
        return name;
    }
}
