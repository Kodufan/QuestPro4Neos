﻿using BaseX;
using FrooxEngine;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace QuestProModule.ALXR
{
    public class ALXRModule : IQuestProModule
    {
        private IPAddress localAddr;
        private const int DEFAULT_PORT = 13191;
        
        private TcpClient client;
        private NetworkStream stream;
        private Thread tcpThread;
        private CancellationTokenSource cancellationTokenSource;
        private bool connected = false;

        private const int NATURAL_EXPRESSIONS_COUNT = 63;
        private const float SRANIPAL_NORMALIZER = 0.75f;
        private byte[] rawExpressions = new byte[NATURAL_EXPRESSIONS_COUNT * 4 + (8 * 2 * 4)];
        private float[] expressions = new float[NATURAL_EXPRESSIONS_COUNT + (8 * 2)];

        private double pitch_L, yaw_L, pitch_R, yaw_R; // Eye rotations

        public async Task<bool> Initialize(string ipconfig)
        {
            try
            {
                localAddr = IPAddress.Parse(ipconfig);

                cancellationTokenSource = new CancellationTokenSource();

                UniLog.Log("Attempting to connect to TCP socket.");
                var connected = await ConnectToTCP(); // This should not block the main thread anymore...?
            } 
            catch (Exception e)
            {
                UniLog.Error(e.Message);
                return false;
            }

            if (connected) 
            {
                tcpThread = new Thread(Update);
                tcpThread.Start();
                return true;
            }

            return false;
        }

        private async Task<bool> ConnectToTCP()
        {
            try
            {
                client = new TcpClient();
                UniLog.Log($"Trying to establish a Quest Pro connection at {localAddr}:{DEFAULT_PORT}...");

                await client.ConnectAsync(localAddr, DEFAULT_PORT);

                if (client.Connected)
                {
                    UniLog.Log("Connected to Quest Pro!");

                    stream = client.GetStream();
                    connected = true;

                    return true;
                } 
                else
                {
                    UniLog.Error("Couldn't connect!");
                    return false;
                }
            }
            catch (Exception e)
            {
                UniLog.Error(e.Message);
                return false;
            }
        }

        public void Update()
        {
            while (!cancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    // Attempt reconnection if needed
                    if (!connected || stream == null)
                    {
                        ConnectToTCP().RunSynchronously();
                    }

                    // If the connection was unsuccessful, wait a bit and try again
                    if (stream == null)
                    {
                        UniLog.Warning("Didn't reconnect to the Quest Pro just yet! Trying again...");
                        return;
                    }

                    if (!stream.CanRead)
                    {
                        UniLog.Warning("Can't read from the Quest Pro network stream just yet! Trying again...");
                        return;
                    }

                    int offset = 0;
                    int readBytes;
                    do
                    {
                        readBytes = stream.Read(rawExpressions, offset, rawExpressions.Length - offset);
                        offset += readBytes;
                    }
                    while (readBytes > 0 && offset < rawExpressions.Length);

                    if (offset < rawExpressions.Length && connected)
                    {
                        UniLog.Warning("End of stream! Reconnecting...");
                        Thread.Sleep(1000);
                        connected = false;
                        try
                        {
                            stream.Close();
                        }
                        catch (SocketException e)
                        {
                            UniLog.Error(e.Message);
                            Thread.Sleep(1000);
                        }
                    }

                    // We receive information from the stream as a byte array 63*4 bytes long, since floats are 32 bits long and we have 63 expressions.
                    // We then need to convert these bytes to a floats. I've opted to use Buffer.BlockCopy instead of BitConverter.ToSingle, since it's faster.
                    // Future note: Testing this against Array.Copy() doesn't seem to show any difference in performance, so I'll stick to this
                    Buffer.BlockCopy(rawExpressions, 0, expressions, 0, NATURAL_EXPRESSIONS_COUNT * 4 + (8 * 2 * 4));

                    // Preprocess our expressions per Meta's Documentation
                    PrepareUpdate();
                }
                catch (SocketException e)
                {
                    UniLog.Error(e.Message);
                    Thread.Sleep(1000);
                }
            }         
        }
    
        private void PrepareUpdate()
        {
            // Eye Expressions

            double q_x = expressions[FBExpression.LeftRot_x];
            double q_y = expressions[FBExpression.LeftRot_y];
            double q_z = expressions[FBExpression.LeftRot_z];
            double q_w = expressions[FBExpression.LeftRot_w];

            double yaw = Math.Atan2(2.0 * (q_y * q_z + q_w * q_x), q_w * q_w - q_x * q_x - q_y * q_y + q_z * q_z);
            double pitch = Math.Asin(-2.0 * (q_x * q_z - q_w * q_y));
            // Not needed for eye tracking
            // double roll = Math.Atan2(2.0 * (q_x * q_y + q_w * q_z), q_w * q_w + q_x * q_x - q_y * q_y - q_z * q_z); 

            // From radians
            pitch_L = 180.0 / Math.PI * pitch; 
            yaw_L = 180.0 / Math.PI * yaw;

            q_x = expressions[FBExpression.RightRot_x];
            q_y = expressions[FBExpression.RightRot_y];
            q_z = expressions[FBExpression.RightRot_z];
            q_w = expressions[FBExpression.RightRot_w];

            yaw = Math.Atan2(2.0 * (q_y * q_z + q_w * q_x), q_w * q_w - q_x * q_x - q_y * q_y + q_z * q_z);
            pitch = Math.Asin(-2.0 * (q_x * q_z - q_w * q_y));

            // From radians
            pitch_R = 180.0 / Math.PI * pitch; 
            yaw_R = 180.0 / Math.PI * yaw;

            // Face Expressions

            // Eyelid edge case, eyes are actually closed now
            if (expressions[FBExpression.Eyes_Look_Down_L] == expressions[FBExpression.Eyes_Look_Up_L] && expressions[FBExpression.Eyes_Closed_L] > 0.25f)
            { 
                expressions[FBExpression.Eyes_Closed_L] = 0; // 0.9f - (expressions[FBExpression.Lid_Tightener_L] * 3);
            }
            else
            {
                expressions[FBExpression.Eyes_Closed_L] = 0.9f - ((expressions[FBExpression.Eyes_Closed_L] * 3) / (1 + expressions[FBExpression.Eyes_Look_Down_L] * 3));
            }

            // Another eyelid edge case
            if (expressions[FBExpression.Eyes_Look_Down_R] == expressions[FBExpression.Eyes_Look_Up_R] && expressions[FBExpression.Eyes_Closed_R] > 0.25f)
            { 
                expressions[FBExpression.Eyes_Closed_R] = 0; // 0.9f - (expressions[FBExpression.Lid_Tightener_R] * 3);
            }
            else
            {
                expressions[FBExpression.Eyes_Closed_R] = 0.9f - ((expressions[FBExpression.Eyes_Closed_R] * 3) / (1 + expressions[FBExpression.Eyes_Look_Down_R] * 3));
            }

            //expressions[FBExpression.Lid_Tightener_L = 0.8f-expressions[FBExpression.Eyes_Closed_L]; // Sad: fix combined param instead
            //expressions[FBExpression.Lid_Tightener_R = 0.8f-expressions[FBExpression.Eyes_Closed_R]; // Sad: fix combined param instead

            if (1 - expressions[FBExpression.Eyes_Closed_L] < expressions[FBExpression.Lid_Tightener_L])
                expressions[FBExpression.Lid_Tightener_L] = (1 - expressions[FBExpression.Eyes_Closed_L]) - 0.01f;

            if (1 - expressions[FBExpression.Eyes_Closed_R] < expressions[FBExpression.Lid_Tightener_R])
                expressions[FBExpression.Lid_Tightener_R] = (1 - expressions[FBExpression.Eyes_Closed_R]) - 0.01f;

            //expressions[FBExpression.Lid_Tightener_L = Math.Max(0, expressions[FBExpression.Lid_Tightener_L] - 0.15f);
            //expressions[FBExpression.Lid_Tightener_R = Math.Max(0, expressions[FBExpression.Lid_Tightener_R] - 0.15f);

            expressions[FBExpression.Upper_Lid_Raiser_L] = Math.Max(0, expressions[FBExpression.Upper_Lid_Raiser_L] - 0.5f);
            expressions[FBExpression.Upper_Lid_Raiser_R] = Math.Max(0, expressions[FBExpression.Upper_Lid_Raiser_R] - 0.5f);

            expressions[FBExpression.Lid_Tightener_L] = Math.Max(0, expressions[FBExpression.Lid_Tightener_L] - 0.5f);
            expressions[FBExpression.Lid_Tightener_R] = Math.Max(0, expressions[FBExpression.Lid_Tightener_R] - 0.5f);

            expressions[FBExpression.Inner_Brow_Raiser_L] = Math.Min(1, expressions[FBExpression.Inner_Brow_Raiser_L] * 3f); // * 4;
            expressions[FBExpression.Brow_Lowerer_L] = Math.Min(1, expressions[FBExpression.Brow_Lowerer_L] * 3f); // * 4;
            expressions[FBExpression.Outer_Brow_Raiser_L] = Math.Min(1, expressions[FBExpression.Outer_Brow_Raiser_L] * 3f); // * 4;

            expressions[FBExpression.Inner_Brow_Raiser_R] = Math.Min(1, expressions[FBExpression.Inner_Brow_Raiser_R] * 3f); // * 4;
            expressions[FBExpression.Brow_Lowerer_R] = Math.Min(1, expressions[FBExpression.Brow_Lowerer_R] * 3f); // * 4;
            expressions[FBExpression.Outer_Brow_Raiser_R] = Math.Min(1, expressions[FBExpression.Outer_Brow_Raiser_R] * 3f); // * 4;

            expressions[FBExpression.Eyes_Look_Up_L] = expressions[FBExpression.Eyes_Look_Up_L] * 0.55f;
            expressions[FBExpression.Eyes_Look_Up_R] = expressions[FBExpression.Eyes_Look_Up_R] * 0.55f;
            expressions[FBExpression.Eyes_Look_Down_L] = expressions[FBExpression.Eyes_Look_Down_L] * 1.5f;
            expressions[FBExpression.Eyes_Look_Down_R] = expressions[FBExpression.Eyes_Look_Down_R] * 1.5f;

            expressions[FBExpression.Eyes_Look_Left_L] = expressions[FBExpression.Eyes_Look_Left_L] * 0.85f;
            expressions[FBExpression.Eyes_Look_Right_L] = expressions[FBExpression.Eyes_Look_Right_L] * 0.85f;
            expressions[FBExpression.Eyes_Look_Left_R] = expressions[FBExpression.Eyes_Look_Left_R] * 0.85f;
            expressions[FBExpression.Eyes_Look_Right_R] = expressions[FBExpression.Eyes_Look_Right_R] * 0.85f;

            // Hack: turn rots to looks
            // Yitch = 29(left)-- > -29(right)
            // Yaw = -27(down)-- > 27(up)

            if (pitch_L > 0)
            {
                expressions[FBExpression.Eyes_Look_Left_L] = Math.Min(1, (float)(pitch_L / 29.0)) * SRANIPAL_NORMALIZER;
                expressions[FBExpression.Eyes_Look_Right_L] = 0;
            }
            else
            {
                expressions[FBExpression.Eyes_Look_Left_L] = 0;
                expressions[FBExpression.Eyes_Look_Right_L] = Math.Min(1, (float)((-pitch_L) / 29.0)) * SRANIPAL_NORMALIZER;
            }

            if (yaw_L > 0)
            {
                expressions[FBExpression.Eyes_Look_Up_L] = Math.Min(1, (float)(yaw_L / 27.0)) * SRANIPAL_NORMALIZER;
                expressions[FBExpression.Eyes_Look_Down_L] = 0;
            }
            else
            {
                expressions[FBExpression.Eyes_Look_Up_L] = 0;
                expressions[FBExpression.Eyes_Look_Down_L] = Math.Min(1, (float)((-yaw_L) / 27.0)) * SRANIPAL_NORMALIZER;
            }


            if (pitch_R > 0)
            {
                expressions[FBExpression.Eyes_Look_Left_R] = Math.Min(1, (float)(pitch_R / 29.0)) * SRANIPAL_NORMALIZER;
                expressions[FBExpression.Eyes_Look_Right_R] = 0;
            }
            else
            {
                expressions[FBExpression.Eyes_Look_Left_R] = 0;
                expressions[FBExpression.Eyes_Look_Right_R] = Math.Min(1, (float)((-pitch_R) / 29.0)) * SRANIPAL_NORMALIZER;
            }
            
            if (yaw_R > 0)
            {
                expressions[FBExpression.Eyes_Look_Up_R] = Math.Min(1, (float)(yaw_R / 27.0)) * SRANIPAL_NORMALIZER;
                expressions[FBExpression.Eyes_Look_Down_R] = 0;
            }
            else
            {
                expressions[FBExpression.Eyes_Look_Up_R] = 0;
                expressions[FBExpression.Eyes_Look_Down_R] = Math.Min(1, (float)((-yaw_R) / 27.0)) * SRANIPAL_NORMALIZER;
            }
        }

        public void Teardown()
        {
            cancellationTokenSource.Cancel();
            tcpThread.Abort();
            cancellationTokenSource.Dispose();
            stream.Close();
            stream.Dispose();
            client.Close();
            client.Dispose();
        }

        bool IsValid(float3 value) => IsValid(value.x) && IsValid(value.y) && IsValid(value.z);

        bool IsValid(float value) => !float.IsInfinity(value) && !float.IsNaN(value);

        public struct EyeGazeData
        {
            public bool isValid;
            public float3 position;
            public floatQ rotation;
            public float open;
            public float squeeze;
            public float wide;
            public float gazeConfidence;
        }

        public EyeGazeData GetEyeData(FBEye fbEye)
        {
            EyeGazeData eyeRet = new EyeGazeData();
            switch (fbEye)
            {
                case FBEye.Left:
                    eyeRet.position = new float3(expressions[FBExpression.LeftPos_x], -expressions[FBExpression.LeftPos_y], expressions[FBExpression.LeftPos_z]);
                    eyeRet.rotation = new floatQ(-expressions[FBExpression.LeftRot_x], -expressions[FBExpression.LeftRot_y], -expressions[FBExpression.LeftRot_z], expressions[FBExpression.LeftRot_w]);
                    eyeRet.open = MathX.Max(0, expressions[FBExpression.Eyes_Closed_L]);
                    eyeRet.squeeze = expressions[FBExpression.Lid_Tightener_L];
                    eyeRet.wide = expressions[FBExpression.Upper_Lid_Raiser_L];
                    eyeRet.isValid = IsValid(eyeRet.position);
                    return eyeRet;
                case FBEye.Right:
                    eyeRet.position = new float3(expressions[FBExpression.RightPos_x], -expressions[FBExpression.RightPos_y], expressions[FBExpression.RightPos_z]);
                    eyeRet.rotation = new floatQ(-expressions[FBExpression.LeftRot_x], -expressions[FBExpression.LeftRot_y], -expressions[FBExpression.LeftRot_z], expressions[FBExpression.RightRot_w]);
                    eyeRet.open = MathX.Max(0, expressions[FBExpression.Eyes_Closed_R]);
                    eyeRet.squeeze = expressions[FBExpression.Lid_Tightener_R];
                    eyeRet.wide = expressions[FBExpression.Upper_Lid_Raiser_R];
                    eyeRet.isValid = IsValid(eyeRet.position);
                    return eyeRet;
                default:
                    throw new Exception($"Invalid eye argument: {fbEye}");
            }
        }

        public void GetEyeExpressions(FBEye fbEye, FrooxEngine.Eye frooxEye)
        {
            frooxEye.PupilDiameter = 0.004f;
            frooxEye.Frown = 0f;

            switch (fbEye)
            {
                case FBEye.Left:
                    frooxEye.UpdateWithRotation(new floatQ(-expressions[FBExpression.LeftRot_x], -expressions[FBExpression.LeftRot_z], -expressions[FBExpression.LeftRot_y], expressions[FBExpression.LeftRot_w]));
                    frooxEye.RawPosition = new float3(expressions[FBExpression.LeftPos_x], expressions[FBExpression.LeftPos_y], expressions[FBExpression.LeftPos_z]);
                    frooxEye.Openness = MathX.Max(0, expressions[FBExpression.Eyes_Closed_L]);
                    frooxEye.Squeeze = expressions[FBExpression.Lid_Tightener_L];
                    frooxEye.Widen = expressions[FBExpression.Upper_Lid_Raiser_L];
                    break;
                case FBEye.Right:
                    frooxEye.UpdateWithRotation(new floatQ(-expressions[FBExpression.RightRot_x], -expressions[FBExpression.RightRot_z], -expressions[FBExpression.RightRot_y], expressions[FBExpression.RightRot_w]));
                    frooxEye.RawPosition = new float3(expressions[FBExpression.RightPos_x], expressions[FBExpression.RightPos_y], expressions[FBExpression.RightPos_z]);
                    frooxEye.Openness = MathX.Max(0, expressions[FBExpression.Eyes_Closed_R]);
                    frooxEye.Squeeze = expressions[FBExpression.Lid_Tightener_R];
                    frooxEye.Widen = expressions[FBExpression.Upper_Lid_Raiser_R];
                    break;
                case FBEye.Combined:
                    frooxEye.UpdateWithRotation(MathX.Slerp(new floatQ(expressions[FBExpression.LeftRot_x], expressions[FBExpression.LeftRot_y], expressions[FBExpression.LeftRot_z], expressions[FBExpression.LeftRot_w]), new floatQ(expressions[FBExpression.RightRot_x], expressions[FBExpression.RightRot_y], expressions[FBExpression.RightRot_z], expressions[FBExpression.RightRot_w]), 0.5f));
                    frooxEye.RawPosition = MathX.Average(new float3(expressions[FBExpression.LeftPos_x], expressions[FBExpression.LeftPos_z], expressions[FBExpression.LeftPos_y]), new float3(expressions[FBExpression.RightPos_x], expressions[FBExpression.RightPos_z], expressions[FBExpression.RightPos_y]));
                    frooxEye.Openness = MathX.Max(0, expressions[FBExpression.Eyes_Closed_R] + expressions[FBExpression.Eyes_Closed_R]) / 2.0f;
                    frooxEye.Squeeze = (expressions[FBExpression.Lid_Tightener_R] + expressions[FBExpression.Lid_Tightener_R]) / 2.0f;
                    frooxEye.Widen = (expressions[FBExpression.Upper_Lid_Raiser_R] + expressions[FBExpression.Upper_Lid_Raiser_R]) / 2.0f;
                    break;
            }
            
            frooxEye.IsTracking = IsValid(frooxEye.RawPosition);
            frooxEye.IsTracking = IsValid(frooxEye.Direction);
            frooxEye.IsTracking = IsValid(frooxEye.Openness);
        }

        // TODO: Double check jaw movements and mappings
        public void GetFacialExpressions(FrooxEngine.Mouth mouth)
        {
            mouth.IsDeviceActive = Engine.Current.InputInterface.VR_Active;
            mouth.IsTracking = Engine.Current.InputInterface.VR_Active;

            mouth.JawOpen = expressions[FBExpression.Jaw_Drop];

            var jawHorizontal = expressions[FBExpression.Jaw_Sideways_Right] - expressions[FBExpression.Jaw_Sideways_Left];
            var jawForward = expressions[FBExpression.Jaw_Thrust];
            var jawDown = expressions[FBExpression.Lips_Toward] + expressions[FBExpression.Jaw_Drop];

            mouth.Jaw = new float3(
                jawHorizontal,
                jawForward,
                jawDown
            );

            mouth.LipUpperLeftRaise = expressions[FBExpression.Upper_Lip_Raiser_L];
            mouth.LipUpperRightRaise = expressions[FBExpression.Upper_Lip_Raiser_R];
            mouth.LipLowerLeftRaise = expressions[FBExpression.Lower_Lip_Depressor_L];
            mouth.LipLowerRightRaise = expressions[FBExpression.Lower_Lip_Depressor_R];

            mouth.LipUpperHorizontal = expressions[FBExpression.Mouth_Right] - expressions[FBExpression.Mouth_Left];
            mouth.LipLowerHorizontal = expressions[FBExpression.Mouth_Right] - expressions[FBExpression.Mouth_Left];

            mouth.MouthLeftSmileFrown = expressions[FBExpression.Lip_Corner_Puller_L] - expressions[FBExpression.Lip_Corner_Depressor_L];
            mouth.MouthRightSmileFrown = expressions[FBExpression.Lip_Corner_Puller_R] - expressions[FBExpression.Lip_Corner_Depressor_R];

            mouth.MouthPout = expressions[FBExpression.Lip_Pucker_L] + expressions[FBExpression.Lip_Pucker_R];

            mouth.LipTopOverturn = expressions[FBExpression.Lip_Funneler_RT] + expressions[FBExpression.Lip_Funneler_LT];
            mouth.LipBottomOverturn = expressions[FBExpression.Lip_Funneler_RB] + expressions[FBExpression.Lip_Funneler_LB];

            mouth.LipTopOverUnder = -(expressions[FBExpression.Lip_Suck_RT] + expressions[FBExpression.Lip_Suck_LT]);
            mouth.LipBottomOverUnder = expressions[FBExpression.Chin_Raiser_B] - (expressions[FBExpression.Lip_Suck_RB] + expressions[FBExpression.Lip_Suck_LB]);

            mouth.CheekLeftPuffSuck = expressions[FBExpression.Cheek_Puff_L];
            mouth.CheekRightPuffSuck = expressions[FBExpression.Cheek_Puff_R];

            mouth.CheekLeftPuffSuck -= expressions[FBExpression.Cheek_Suck_L];
            mouth.CheekRightPuffSuck -= expressions[FBExpression.Cheek_Suck_R];

        }

        public float GetFaceExpression(int expressionIndex)
        {
            return expressions[expressionIndex];
        }

        public enum FBEye
        {
            Left,
            Right,
            Combined
        }
    }
}
