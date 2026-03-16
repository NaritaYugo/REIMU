using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class PlayerController : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 5f;
    public float jumpForce = 20f;

    [Header("Camera Tracking & Look")]
    public Transform cameraTransform;
    public Vector3 cameraOffset = new Vector3(0, 2f, -5f); // Z値がキャラクターからの距離になります
    public float cameraFollowSpeed = 10f; // 追従速度を少し上げてキビキビさせます
    public float lookSpeedX = 150f; // 左右の視点移動スピード
    public float lookSpeedY = 100f; // 上下の視点移動スピード

    private CharacterController controller;
    private float verticalVelocity = 0f;
    private float cameraPitch = 0f; // カメラの上下角度

    void Start()
    {
        controller = GetComponent<CharacterController>();
        
        if (cameraTransform == null && Camera.main != null)
        {
            cameraTransform = Camera.main.transform;
        }
    }

    void Update()
    {
        // --- 1. 視点移動処理（右クリックドラッグ） ---
        if (Input.GetMouseButton(1))
        {
            // 左右の視点移動：キャラクター自身の向きを変える
            float mouseX = Input.GetAxis("Mouse X");
            transform.Rotate(0, mouseX * lookSpeedX * Time.deltaTime, 0);

            // 上下の視点移動：カメラのピッチ角を更新
            float mouseY = Input.GetAxis("Mouse Y");
            cameraPitch -= mouseY * lookSpeedY * Time.deltaTime;
            
            // 真上・真下を向きすぎてカメラがひっくり返るのを防ぐ
            cameraPitch = Mathf.Clamp(cameraPitch, -80f, 80f);
        }

        // --- 2. キャラクターの移動処理（WASD） ---
        float h = Input.GetAxis("Horizontal"); // A/D または 左右矢印
        float v = Input.GetAxis("Vertical");   // W/S または 上下矢印

        // キャラクターの向いている方向を基準に、前後(v)と左右(h)のベクトルを合成
        Vector3 move = transform.forward * v + transform.right * h;
        
        // 斜め移動（WとAを同時押しなど）の時に速度が上がらないように正規化
        if (move.magnitude > 1f) move.Normalize();
        move *= moveSpeed;

        // --- 3. 重力とジャンプの処理 ---
        if (controller.isGrounded) {
            verticalVelocity = -0.5f; 

            if (Input.GetButtonDown("Jump")) {
                verticalVelocity = jumpForce;
            }
        } else {
            verticalVelocity -= 9.81f * Time.deltaTime;
        }
        
        move.y = verticalVelocity;
        controller.Move(move * Time.deltaTime);
    }

    void LateUpdate()
    {
        // --- 4. カメラの追従処理（TPSカメラ方式） ---
        if (cameraTransform != null)
        {
            // キャラクターの向き（Yaw）と、計算した上下角度（Pitch）を合成した回転
            Quaternion camRotation = transform.rotation * Quaternion.Euler(cameraPitch, 0f, 0f);

            // キャラクターの頭のあたりを回転の支点（ピボット）にする
            Vector3 pivot = transform.position + Vector3.up * cameraOffset.y;

            // 支点から、指定された距離（cameraOffset.z）だけ背後にカメラを配置
            Vector3 targetPos = pivot + (camRotation * new Vector3(0, 0, cameraOffset.z));
            
            // 滑らかに位置と回転を追従
            cameraTransform.position = Vector3.Lerp(cameraTransform.position, targetPos, cameraFollowSpeed * Time.deltaTime);
            cameraTransform.rotation = Quaternion.Lerp(cameraTransform.rotation, camRotation, cameraFollowSpeed * Time.deltaTime);
        }
    }
}