using System;
using UnityEngine;
using UnityEngine.Events;

public class ParticleControl : MonoBehaviour
{
    private PlayerController player;
    private ParticleSystem particleSystem;
    private void Start() {
        particleSystem = GetComponent<ParticleSystem>();    
        player = PlayerController.Instance;
        player.OnSpinStart += PlayerController_OnSpinStart;

        player.OnSpinCancel += PlayerController_OnSpinCancel;

        particleSystem.Pause();
    }

    private void PlayerController_OnSpinStart(object sender, System.EventArgs e) {
        particleSystem.Play();
    }


private void PlayerController_OnSpinCancel(object sender, System.EventArgs e) {
    particleSystem.Pause();
        particleSystem.Clear();
}
}
