using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;

namespace AssetStudioGUI
{
    /// <summary>
    /// Isolated, read-only static geometry viewer for one serialized Unity Mesh.
    /// Rig, textures, materials, animation, and effects are intentionally not represented here yet.
    /// </summary>
    public sealed class JuanchoVisualLabMeshPreviewForm : Form
    {
        private readonly Juancho.VisualLab.VisualLabMeshPreviewData _mesh;
        private readonly OpenTK.GLControl _gl = new OpenTK.GLControl();
        private readonly ToolStripButton _wireframeButton = new ToolStripButton("Wireframe") { CheckOnClick = true };
        private readonly ToolStripButton _normalButton = new ToolStripButton("Flat lighting") { CheckOnClick = true };
        private readonly ToolStripStatusLabel _status = new ToolStripStatusLabel();
        private readonly ToolStripStatusLabel _help = new ToolStripStatusLabel();

        private int _program;
        private int _vao;
        private int _positionBuffer;
        private int _normalBuffer;
        private int _indexBuffer;
        private int _positionLocation;
        private int _normalLocation;
        private int _modelLocation;
        private int _viewLocation;
        private int _projectionLocation;
        private bool _glReady;
        private bool _leftDragging;
        private bool _panDragging;
        private int _lastMouseX;
        private int _lastMouseY;
        private float _yaw = -0.55f;
        private float _pitch = -0.32f;
        private float _distance = 3.2f;
        private float _panX;
        private float _panY;
        private Matrix4 _modelMatrix = Matrix4.Identity;
        private Vector3[] _positions = Array.Empty<Vector3>();
        private Vector3[] _normals = Array.Empty<Vector3>();
        private uint[] _indices = Array.Empty<uint>();

        public JuanchoVisualLabMeshPreviewForm(Juancho.VisualLab.VisualLabMeshPreviewData mesh)
        {
            _mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
            if (mesh.Positions == null || mesh.Normals == null || mesh.Indices == null ||
                mesh.Positions.Length < 9 || mesh.Normals.Length != mesh.Positions.Length || mesh.Indices.Length < 3)
                throw new ArgumentException("The selected Mesh does not contain usable triangle geometry.", nameof(mesh));

            Text = "Juancho MLBB Visual Lab — Static Mesh Preview — " + (string.IsNullOrWhiteSpace(mesh.Name) ? "Mesh" : mesh.Name);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(760, 500);
            Size = new Size(1080, 760);
            KeyPreview = true;

            var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
            var resetButton = new ToolStripButton("Reset View");
            resetButton.Click += (s, e) => ResetView();
            _wireframeButton.CheckedChanged += (s, e) => _gl.Invalidate();
            _normalButton.CheckedChanged += (s, e) => _gl.Invalidate();
            tools.Items.Add(resetButton);
            tools.Items.Add(new ToolStripSeparator());
            tools.Items.Add(_wireframeButton);
            tools.Items.Add(_normalButton);
            tools.Items.Add(new ToolStripLabel("Left drag: rotate  ·  Right/middle drag: pan  ·  Wheel: zoom"));

            var statusStrip = new StatusStrip();
            _status.Text = mesh.VertexCount.ToString("N0") + " vertices · " + mesh.TriangleCount.ToString("N0") + " triangles";
            _help.Text = "Static geometry only — materials, textures, skeleton deformation, AnimationClips, and effects are not yet rendered";
            statusStrip.Items.Add(_status);
            statusStrip.Items.Add(new ToolStripStatusLabel { Spring = true });
            statusStrip.Items.Add(_help);

            _gl.Dock = DockStyle.Fill;
            _gl.BackColor = Color.FromArgb(20, 23, 30);
            _gl.Load += Gl_Load;
            _gl.Paint += Gl_Paint;
            _gl.Resize += (s, e) => _gl.Invalidate();
            _gl.MouseDown += Gl_MouseDown;
            _gl.MouseUp += Gl_MouseUp;
            _gl.MouseMove += Gl_MouseMove;
            _gl.MouseWheel += Gl_MouseWheel;
            _gl.DoubleClick += (s, e) => ResetView();

            Controls.Add(_gl);
            Controls.Add(tools);
            Controls.Add(statusStrip);
        }

        private void Gl_Load(object sender, EventArgs e)
        {
            try
            {
                _gl.MakeCurrent();
                BuildCpuGeometry();
                InitializeOpenGl();
                CreateBuffers();
                _glReady = true;
                _status.Text = _mesh.VertexCount.ToString("N0") + " vertices · " + _mesh.TriangleCount.ToString("N0") + " triangles";
                _help.Text = "Source: " + System.IO.Path.GetFileName(_mesh.SourceBundlePath) + " · Path ID " + _mesh.PathId;
                _gl.Invalidate();
            }
            catch (Exception ex)
            {
                _glReady = false;
                _help.Text = "Viewport initialization failed";
                MessageBox.Show(this, "The static mesh could not be rendered. Source bundle was not changed.\r\n\r\n" + ex.Message,
                    "Juancho MLBB Visual Lab", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void BuildCpuGeometry()
        {
            var count = _mesh.Positions.Length / 3;
            _positions = new Vector3[count];
            _normals = new Vector3[count];
            var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (var i = 0; i < count; i++)
            {
                var p = new Vector3(_mesh.Positions[i * 3], _mesh.Positions[i * 3 + 1], _mesh.Positions[i * 3 + 2]);
                _positions[i] = p;
                min.X = Math.Min(min.X, p.X); min.Y = Math.Min(min.Y, p.Y); min.Z = Math.Min(min.Z, p.Z);
                max.X = Math.Max(max.X, p.X); max.Y = Math.Max(max.Y, p.Y); max.Z = Math.Max(max.Z, p.Z);

                var n = new Vector3(_mesh.Normals[i * 3], _mesh.Normals[i * 3 + 1], _mesh.Normals[i * 3 + 2]);
                _normals[i] = n.LengthSquared > 1e-10f ? Vector3.Normalize(n) : Vector3.UnitY;
            }

            var center = (min + max) * 0.5f;
            var diagonal = (max - min).Length;
            var scale = 2.0f / Math.Max(diagonal, 1e-5f);
            _modelMatrix = Matrix4.CreateTranslation(-center) * Matrix4.CreateScale(scale);
            _indices = _mesh.Indices;
        }

        private void InitializeOpenGl()
        {
            GL.ClearColor(Color.FromArgb(20, 23, 30));
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.Disable(EnableCap.CullFace);

            const string vertexSource = @"#version 330 core
in vec3 vertexPosition;
in vec3 normalDirection;
uniform mat4 modelMatrix;
uniform mat4 viewMatrix;
uniform mat4 projMatrix;
out vec3 fragNormal;
void main()
{
    gl_Position = projMatrix * viewMatrix * modelMatrix * vec4(vertexPosition, 1.0);
    fragNormal = normalize(mat3(modelMatrix) * normalDirection);
}";
            const string fragmentSource = @"#version 330 core
in vec3 fragNormal;
uniform int flatLighting;
out vec4 outputColor;
void main()
{
    vec3 baseColor = vec3(0.69, 0.75, 0.84);
    if (flatLighting != 0)
    {
        outputColor = vec4(baseColor, 1.0);
        return;
    }
    vec3 n = normalize(fragNormal);
    vec3 lightDir = normalize(vec3(-0.45, 0.78, 0.61));
    float diffuse = max(dot(n, lightDir), 0.0);
    float light = 0.24 + diffuse * 0.76;
    outputColor = vec4(baseColor * light, 1.0);
}";
            var vertex = CompileShader(ShaderType.VertexShader, vertexSource);
            var fragment = CompileShader(ShaderType.FragmentShader, fragmentSource);
            _program = GL.CreateProgram();
            GL.AttachShader(_program, vertex);
            GL.AttachShader(_program, fragment);
            GL.LinkProgram(_program);
            GL.GetProgram(_program, GetProgramParameterName.LinkStatus, out var linked);
            var linkLog = GL.GetProgramInfoLog(_program);
            GL.DeleteShader(vertex);
            GL.DeleteShader(fragment);
            if (linked == 0)
                throw new InvalidOperationException("Viewport shader linking failed: " + linkLog);

            _positionLocation = GL.GetAttribLocation(_program, "vertexPosition");
            _normalLocation = GL.GetAttribLocation(_program, "normalDirection");
            _modelLocation = GL.GetUniformLocation(_program, "modelMatrix");
            _viewLocation = GL.GetUniformLocation(_program, "viewMatrix");
            _projectionLocation = GL.GetUniformLocation(_program, "projMatrix");
        }

        private static int CompileShader(ShaderType type, string source)
        {
            var shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out var compiled);
            var log = GL.GetShaderInfoLog(shader);
            if (compiled == 0)
            {
                GL.DeleteShader(shader);
                throw new InvalidOperationException(type + " compilation failed: " + log);
            }
            return shader;
        }

        private void CreateBuffers()
        {
            GL.GenVertexArrays(1, out _vao);
            GL.BindVertexArray(_vao);

            GL.GenBuffers(1, out _positionBuffer);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _positionBuffer);
            GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(_positions.Length * Vector3.SizeInBytes), _positions, BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(_positionLocation, 3, VertexAttribPointerType.Float, false, 0, IntPtr.Zero);
            GL.EnableVertexAttribArray(_positionLocation);

            GL.GenBuffers(1, out _normalBuffer);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _normalBuffer);
            GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(_normals.Length * Vector3.SizeInBytes), _normals, BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(_normalLocation, 3, VertexAttribPointerType.Float, false, 0, IntPtr.Zero);
            GL.EnableVertexAttribArray(_normalLocation);

            GL.GenBuffers(1, out _indexBuffer);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
            GL.BufferData(BufferTarget.ElementArrayBuffer, (IntPtr)(_indices.Length * sizeof(uint)), _indices, BufferUsageHint.StaticDraw);

            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            GL.BindVertexArray(0);
        }

        private void Gl_Paint(object sender, PaintEventArgs e)
        {
            if (!_glReady || _gl.IsDisposed || _gl.ClientSize.Width <= 0 || _gl.ClientSize.Height <= 0)
                return;
            try
            {
                _gl.MakeCurrent();
                GL.Viewport(0, 0, _gl.ClientSize.Width, _gl.ClientSize.Height);
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                GL.UseProgram(_program);

                var view = Matrix4.CreateTranslation(_panX, _panY, -_distance) *
                           Matrix4.CreateRotationX(_pitch) *
                           Matrix4.CreateRotationY(_yaw);
                var aspect = Math.Max(0.01f, (float)_gl.ClientSize.Width / _gl.ClientSize.Height);
                var projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(42f), aspect, 0.01f, 100f);

                GL.UniformMatrix4(_modelLocation, false, ref _modelMatrix);
                GL.UniformMatrix4(_viewLocation, false, ref view);
                GL.UniformMatrix4(_projectionLocation, false, ref projection);
                GL.Uniform1(GL.GetUniformLocation(_program, "flatLighting"), _normalButton.Checked ? 1 : 0);

                GL.BindVertexArray(_vao);
                GL.PolygonMode(MaterialFace.FrontAndBack, _wireframeButton.Checked ? PolygonMode.Line : PolygonMode.Fill);
                GL.DrawElements(BeginMode.Triangles, _indices.Length, DrawElementsType.UnsignedInt, IntPtr.Zero);
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
                GL.BindVertexArray(0);
                GL.UseProgram(0);
                _gl.SwapBuffers();
            }
            catch (Exception ex)
            {
                _help.Text = "Viewport draw failed: " + ex.Message;
            }
        }

        private void Gl_MouseDown(object sender, MouseEventArgs e)
        {
            _gl.Focus();
            _lastMouseX = e.X;
            _lastMouseY = e.Y;
            _leftDragging = e.Button == MouseButtons.Left;
            _panDragging = e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle;
        }

        private void Gl_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) _leftDragging = false;
            if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle) _panDragging = false;
        }

        private void Gl_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_leftDragging && !_panDragging) return;
            var dx = e.X - _lastMouseX;
            var dy = e.Y - _lastMouseY;
            _lastMouseX = e.X;
            _lastMouseY = e.Y;

            if (_leftDragging)
            {
                _yaw += dx * 0.01f;
                _pitch = Math.Max(-1.52f, Math.Min(1.52f, _pitch + dy * 0.01f));
            }
            else
            {
                var pan = _distance * 0.0017f;
                _panX += dx * pan;
                _panY -= dy * pan;
            }
            _gl.Invalidate();
        }

        private void Gl_MouseWheel(object sender, MouseEventArgs e)
        {
            _distance *= e.Delta > 0 ? 0.88f : 1.14f;
            _distance = Math.Max(0.35f, Math.Min(40f, _distance));
            _gl.Invalidate();
        }

        private void ResetView()
        {
            _yaw = -0.55f;
            _pitch = -0.32f;
            _distance = 3.2f;
            _panX = 0;
            _panY = 0;
            _gl.Invalidate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try
            {
                if (_glReady && !_gl.IsDisposed)
                {
                    _gl.MakeCurrent();
                    if (_indexBuffer != 0) GL.DeleteBuffer(_indexBuffer);
                    if (_normalBuffer != 0) GL.DeleteBuffer(_normalBuffer);
                    if (_positionBuffer != 0) GL.DeleteBuffer(_positionBuffer);
                    if (_vao != 0) GL.DeleteVertexArray(_vao);
                    if (_program != 0) GL.DeleteProgram(_program);
                }
            }
            catch { }
            _glReady = false;
            base.OnFormClosing(e);
        }
    }
}
