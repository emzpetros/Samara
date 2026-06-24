using System;
using UnityEngine;
using UnityEngine.Rendering;

public class SoundManager : MonoBehaviour
{
    public static SoundManager Instance {  get; private set; }
    [SerializeField] private AudioClipRefSO audioClipRefSO;

    private PlayerController player;


    private float volume = 1f;

    private void Awake() {
        Instance = this;
    }


    private void Start() {
        player = PlayerController.Instance;

        player.OnSpinStart += Player_OnSpinStart;

        player.OnObstacleHit += Player_OnObstacleHit;
    }

    private void Player_OnObstacleHit(object sender, EventArgs e) {

        PlaySound(audioClipRefSO.bump, player.transform.position, 0.5f);
    }

    private void Player_OnSpinStart(object sender, EventArgs e) {
        PlaySound(audioClipRefSO.windWhoosh, player.transform.position, 0.5f);
    }

    private void PlaySound(AudioClip audioClip, Vector3 pos, float volumeMult = 1f) {
        AudioSource.PlayClipAtPoint(audioClip, pos, volumeMult * volume);
    }

}
