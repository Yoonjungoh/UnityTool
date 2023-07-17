using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class TestScript : MonoBehaviour
{
    void Start()
    {
        Managers.Resource.Instantiate("Triangle");
        StartCoroutine(NextScene());
    }

    void Update()
    {
        
    }
    IEnumerator NextScene()
    {
        yield return new WaitForSeconds(3f);
        Managers.Scene.LoadScene("NextScene");
    }
}
