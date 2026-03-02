using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class SimplePlayerController : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 5f;
    public float turnSpeed = 200f;

    [Header("Camera Tracking")]
    public Transform cameraTransform;
    public Vector3 cameraOffset = new Vector3(0, 2f, -5f); // キャラクターに対するカメラの相対位置
    public float cameraFollowSpeed = 5f;

    private CharacterController controller;
    private float verticalVelocity = 0f;

    void Start()
    {
        controller = GetComponent<CharacterController>();
        
        // カメラが未設定ならメインカメラを自動取得
        if (cameraTransform == null && Camera.main != null)
        {
            cameraTransform = Camera.main.transform;
        }
    }

    void Update()
    {
        // --- 1. キャラクターの移動処理 ---
        float h = Input.GetAxis("Horizontal"); // A/D または 左右矢印
        float v = Input.GetAxis("Vertical");   // W/S または 上下矢印

        // 旋回
        transform.Rotate(0, h * turnSpeed * Time.deltaTime, 0);

        // 前後移動ベクトル
        Vector3 move = transform.forward * v * moveSpeed;

        // 簡易的な重力
        if (controller.isGrounded) {
            verticalVelocity = -0.5f; // 接地時は微小な下向きの力をかけ続ける
        } else {
            verticalVelocity -= 9.81f * Time.deltaTime;
        }
        move.y = verticalVelocity;

        // 移動実行
        controller.Move(move * Time.deltaTime);
    }

    void LateUpdate()
    {
        // --- 2. カメラの追従処理 ---
        // キャラクターの移動が終わった後(LateUpdate)にカメラを動かすことでガタつきを防ぐ
        if (cameraTransform != null)
        {
            // 目標位置（キャラクターの背後・上空）
            Vector3 targetPos = transform.position + (transform.rotation * cameraOffset);
            
            // 滑らかに追従
            cameraTransform.position = Vector3.Lerp(cameraTransform.position, targetPos, cameraFollowSpeed * Time.deltaTime);
            
            // カメラをキャラクターの少し上(頭のあたり)に向ける
            cameraTransform.LookAt(transform.position + Vector3.up * 1f);
        }
    }
}