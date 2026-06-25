using System;
using UnityEngine;
using UnityEngine.Rendering;

public class SoundManager : MonoBehaviour
{
    public static SoundManager Instance {  get; private set; }
    [SerializeField] private AudioClipRefSO audioClipRefSO;

    private PlayerController player;
    private GameManager gameManager;


    private float volume = 1f;

    private void Awake() {
        Instance = this;
    }


    private void Start() {
        player = PlayerController.Instance;
        gameManager = GameManager.Instance;

        player.OnSpinStart += Player_OnSpinStart;

        player.OnObstacleHit += Player_OnObstacleHit;
        gameManager.OnLevelComplete += SoundManager_OnLevelComplete;
        player.OnNoLift += SoundManager_OnGameOver;
        player.OnLiftPickup += SoundManager_OnPickup;
        player.OnLowLift += Player_OnLowLift;
    }

    private void Player_OnLowLift(object sender, EventArgs e) {

        PlaySound(audioClipRefSO.warningBeep, player.transform.position, 0.5f);
    }

    private void SoundManager_OnPickup(object sender, EventArgs e) {
        PlaySound(audioClipRefSO.pickup, player.transform.position, 0.5f);
    }

    private void SoundManager_OnGameOver(object sender, EventArgs e) {
        PlaySound(audioClipRefSO.gameOver, player.transform.position, 0.5f);
    }

    private void SoundManager_OnLevelComplete(object sender, EventArgs e) {
        PlaySound(audioClipRefSO.victory, player.transform.position, 0.5f);
    }

    private void Player_OnObstacleHit(object sender, EventArgs e) {

        PlaySound(audioClipRefSO.bump, player.transform.position, 0.5f);
        Debug.Log("hit");
    }

    private void Player_OnSpinStart(object sender, EventArgs e) {
        PlaySound(audioClipRefSO.windWhoosh, player.transform.position, 0.1f);
    }

    private void PlaySound(AudioClip audioClip, Vector3 pos, float volumeMult = 1f) {
        AudioSource.PlayClipAtPoint(audioClip, pos, volumeMult * volume);
    }

}
