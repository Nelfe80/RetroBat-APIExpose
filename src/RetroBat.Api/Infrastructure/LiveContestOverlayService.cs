using System.Drawing;
using System.Windows.Forms;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Petite fenetre Live Contest en surimpression du jeu (coin bas-droit) :
/// decompte avant le depart, GO, bravo final... Pilotee en local par le
/// client de jeu (POST /api/v1/overlay/livecontest). La fenetre est topmost,
/// sans bordure et NE PREND JAMAIS le focus (WS_EX_NOACTIVATE) pour ne pas
/// sortir RetroArch du premier plan.
/// </summary>
public sealed class LiveContestOverlayService : IDisposable
{
    private readonly object _gate = new();
    private OverlayForm? _form;
    private Thread? _uiThread;

    public void Show(string? title, string text, string? sub, int? durationMs)
    {
        EnsureForm();
        _form!.BeginInvoke(() => _form.Present(
            string.IsNullOrWhiteSpace(title) ? "LIVE CONTEST" : title!,
            text, sub ?? "", durationMs, center: false));
    }

    /// <summary>
    /// Scene CENTRALE (« Tiens-toi prêt ! », decompte 5..1, GO!) : grande
    /// fenetre au centre de l'ecran, gros chiffres, fondu a chaque message.
    /// </summary>
    public void ShowCenter(string text, string? sub, int? durationMs)
    {
        EnsureForm();
        _form!.BeginInvoke(() => _form.Present(
            "LIVE CONTEST", text, sub ?? "", durationMs, center: true));
    }

    public void Hide()
    {
        var form = _form;
        if (form is { IsHandleCreated: true })
        {
            form.BeginInvoke(form.Conceal);
        }
    }

    private void EnsureForm()
    {
        lock (_gate)
        {
            if (_form is { IsDisposed: false })
            {
                return;
            }

            using var ready = new ManualResetEventSlim();
            _uiThread = new Thread(() =>
            {
                _form = new OverlayForm();
                // cree le handle sans afficher la fenetre
                _ = _form.Handle;
                ready.Set();
                Application.Run();
            })
            {
                IsBackground = true,
                Name = "livecontest-overlay"
            };
            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
        }
    }

    public void Dispose()
    {
        var form = _form;
        if (form is { IsHandleCreated: true })
        {
            form.BeginInvoke(() => { form.Close(); Application.ExitThread(); });
        }
    }

    private sealed class OverlayForm : Form
    {
        private const int WsExTopmost = 0x00000008;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExNoActivate = 0x08000000;

        private readonly Label _brand;
        private readonly Label _text;
        private readonly Label _sub;
        private readonly PictureBox? _bigIcon;
        private readonly System.Windows.Forms.Timer _hideTimer;
        private readonly System.Windows.Forms.Timer _fadeTimer;
        private readonly Font _cornerFont = new("Segoe UI", 15f, FontStyle.Bold);
        private readonly Font _centerPhraseFont = new("Segoe UI", 30f, FontStyle.Bold);
        private readonly Font _cornerSubFont = new("Segoe UI", 9f);
        private readonly Font _centerSubFont = new("Segoe UI", 11.5f);
        // decompte « jeu moderne » : le chiffre grossit en apparaissant
        private readonly Font[] _digitFonts = Enumerable.Range(0, 13)
            .Select(i => new Font("Segoe UI", 72f + i * 10f, FontStyle.Bold))
            .ToArray();
        private int _animStep;
        private bool _animGrow;
        private double _targetOpacity = 1;

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.Black;
            Padding = new Padding(1);
            Size = new Size(380, 104);



            PictureBox? icon = null;
            var iconPath = Path.Combine(AppContext.BaseDirectory, "media", "livecontest-icon.png");
            if (File.Exists(iconPath))
            {
                icon = new PictureBox
                {
                    Image = Image.FromFile(iconPath),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Size = new Size(20, 20),
                    Location = new Point(14, 10),
                    BackColor = Color.Transparent
                };
            }

            _brand = new Label
            {
                Text = "LIVE CONTEST",
                ForeColor = Color.FromArgb(139, 92, 246),  // violet RetroCreator
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(icon is null ? 14 : 40, 12),
                BackColor = Color.Transparent
            };

            _text = new Label
            {
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 15f, FontStyle.Bold),
                AutoSize = false,
                Size = new Size(352, 34),
                Location = new Point(14, 34),
                BackColor = Color.Transparent
            };

            _sub = new Label
            {
                ForeColor = Color.FromArgb(138, 138, 165),
                Font = new Font("Segoe UI", 9f),
                AutoSize = false,
                Size = new Size(352, 20),
                Location = new Point(14, 70),
                BackColor = Color.Transparent
            };

            var bigIconPath = Path.Combine(AppContext.BaseDirectory, "media", "livecontest-icon-big.png");
            if (File.Exists(bigIconPath))
            {
                _bigIcon = new PictureBox
                {
                    Image = Image.FromFile(bigIconPath),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Size = new Size(96, 96),
                    Visible = false,
                    BackColor = Color.Transparent
                };
                Controls.Add(_bigIcon);
            }

            if (icon is not null)
            {
                Controls.Add(icon);
            }

            Controls.Add(_brand);
            Controls.Add(_text);
            Controls.Add(_sub);


            _hideTimer = new System.Windows.Forms.Timer();
            _hideTimer.Tick += (_, _) => Conceal();

            // chiffres : grossissement + fade-in puis fade-out avant le suivant ;
            // phrases : simple fade-in
            _fadeTimer = new System.Windows.Forms.Timer { Interval = 25 };
            _fadeTimer.Tick += (_, _) =>
            {
                _animStep++;
                if (_animStep <= 12)
                {
                    Opacity = Math.Min(_targetOpacity, 0.15 + _animStep * _targetOpacity * 0.08);
                    if (_animGrow)
                    {
                        _text.Font = _digitFonts[Math.Min(_digitFonts.Length - 1, _animStep)];
                    }

                    if (_animStep == 12 && !_animGrow)
                    {
                        Opacity = _targetOpacity;
                        _fadeTimer.Stop();
                    }
                }
                else if (_animGrow && _animStep >= 27)
                {
                    // fade-out : le chiffre s'efface avant le suivant
                    Opacity = Math.Max(0.05, _targetOpacity - (_animStep - 27) * 0.09);
                    if (_animStep >= 38)
                    {
                        _fadeTimer.Stop();
                    }
                }
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // coins arrondis (Windows 11)
            var preference = 2; // DWMWCP_ROUND
            _ = DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int));
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref int value, int size);

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WsExTopmost | WsExToolWindow | WsExNoActivate;
                return cp;
            }
        }

        public void Present(string brand, string text, string sub, int? durationMs, bool center)
        {
            _brand.Text = brand.ToUpperInvariant();
            _text.Text = text;
            _sub.Text = sub;
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            if (center)
            {
                // chiffres/GO : enorme et anime ; phrases : police adaptee (rien de coupe)
                var digit = text.Trim().Length <= 4;
                Size = digit ? new Size(780, 600) : new Size(700, 280);
                _brand.Location = new Point((Width - _brand.PreferredWidth) / 2, 16);
                _text.Font = digit ? _digitFonts[0] : _centerPhraseFont;
                _text.TextAlign = ContentAlignment.MiddleCenter;
                if (_bigIcon is not null)
                {
                    _bigIcon.Visible = !digit;
                    _bigIcon.Location = new Point(34, (Height - 96) / 2 - 10);
                }

                var textLeft = digit || _bigIcon is null ? 10 : 146;
                _text.SetBounds(textLeft, 40, Width - textLeft - 10, digit ? 490 : 150);
                _sub.Font = _centerSubFont;
                _sub.TextAlign = ContentAlignment.MiddleCenter;
                _sub.SetBounds(10, digit ? 538 : 200, Width - 20, 54);
                Location = new Point(
                    area.Left + (area.Width - Width) / 2,
                    area.Top + (area.Height - Height) / 2);
                _animGrow = digit;
            }
            else
            {
                Size = new Size(380, 112);
                _brand.Location = new Point(_brand.Left <= 14 ? 14 : 40, 12);
                _text.Font = _cornerFont;
                _text.TextAlign = ContentAlignment.TopLeft;
                _text.SetBounds(14, 34, 352, 34);
                _sub.Font = _cornerSubFont;
                _sub.TextAlign = ContentAlignment.TopLeft;
                _sub.SetBounds(14, 68, 352, 38); // deux lignes : plus de texte coupe
                Location = new Point(area.Right - Width - 24, area.Bottom - Height - 24);
                if (_bigIcon is not null)
                {
                    _bigIcon.Visible = false;
                }

                _animGrow = false;
            }

            _targetOpacity = 0.9;
            Opacity = 0.1;
            _animStep = 0;
            _fadeTimer.Stop();
            _fadeTimer.Start();
            if (!Visible)
            {
                Show();
            }

            _hideTimer.Stop();
            if (durationMs is > 0)
            {
                _hideTimer.Interval = durationMs.Value;
                _hideTimer.Start();
            }
        }

        public void Conceal()
        {
            _hideTimer.Stop();
            if (Visible)
            {
                Hide();
            }
        }
    }
}
