using System;
using UnityEngine;

public class Obstacle : MonoBehaviour
{
    [SerializeField] private const float LIFT_SUBTRACT_AMOUNT = -1f;



    private void OnTriggerEnter(Collider other) {
        PlayerController player;
        if (other.gameObject.TryGetComponent<PlayerController>(out player)) {
            player.ObstacleHit(LIFT_SUBTRACT_AMOUNT);
        }


    }
}
