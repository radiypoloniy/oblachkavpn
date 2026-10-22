package main

// Выход sing-box типа "amneziawg": AmneziaWG (WireGuard с обфускацией рукопожатия) на официальной
// библиотеке amneziawg-go. Устройство живёт в памяти: TCP/IP-стек gVisor из её tun/netstack,
// соединения программ открываются в нём, а наружу уходят только UDP-пакеты WireGuard к серверу.
// UDP-сокет к серверу открывает набиратель sing-box — с привязкой к физическому интерфейсу, как у
// остальных выходов (auto_detect_interface), иначе пакеты ушли бы обратно в TUN.

import (
	"context"
	"encoding/base64"
	"encoding/hex"
	"net"
	"net/netip"
	"strconv"
	"strings"
	"sync"

	"github.com/amnezia-vpn/amneziawg-go/v3/conn"
	"github.com/amnezia-vpn/amneziawg-go/v3/device"
	"github.com/amnezia-vpn/amneziawg-go/v3/tun/netstack"
	"github.com/sagernet/sing-box/adapter"
	"github.com/sagernet/sing-box/adapter/outbound"
	"github.com/sagernet/sing-box/common/dialer"
	"github.com/sagernet/sing-box/log"
	"github.com/sagernet/sing-box/option"
	E "github.com/sagernet/sing/common/exceptions"
	M "github.com/sagernet/sing/common/metadata"
	N "github.com/sagernet/sing/common/network"
)

const TypeAmneziaWG = "amneziawg"

type AmneziaWGOptions struct {
	Server        string   `json:"server"`
	ServerPort    uint16   `json:"server_port"`
	PrivateKey    string   `json:"private_key"`
	PeerPublicKey string   `json:"peer_public_key"`
	PreSharedKey  string   `json:"pre_shared_key,omitempty"`
	LocalAddress  []string `json:"local_address"`
	MTU           int      `json:"mtu,omitempty"`
	Keepalive     int      `json:"persistent_keepalive_interval,omitempty"`
	// Параметры обфускации AmneziaWG как в её конфигурации: jc, jmin, jmax, s1–s4, h1–h4, i1–i5.
	Obfuscation map[string]string `json:"obfuscation,omitempty"`
}

// Ключи обфускации, которые принимает устройство. Остальные строки UAPI (ключи, адрес сервера,
// разрешённые адреса) задаёт только этот файл.
var amneziaKeys = map[string]bool{"jc": true, "jmin": true, "jmax": true, "s1": true, "s2": true, "s3": true, "s4": true,
	"h1": true, "h2": true, "h3": true, "h4": true, "i1": true, "i2": true, "i3": true, "i4": true, "i5": true}

type AmneziaWGOutbound struct {
	outbound.Adapter
	logger    log.ContextLogger
	keepalive string
	device    *device.Device
	stack     *netstack.Net
	bind      *packetBind
}

func RegisterAmneziaWGOutbound(registry *outbound.Registry) {
	outbound.Register[AmneziaWGOptions](registry, TypeAmneziaWG, NewAmneziaWGOutbound)
}

func NewAmneziaWGOutbound(ctx context.Context, router adapter.Router, logger log.ContextLogger, tag string, options AmneziaWGOptions) (adapter.Outbound, error) {
	uapi, err := amneziaUAPI(options)
	if err != nil {
		return nil, err
	}
	var local []netip.Addr
	for _, prefix := range options.LocalAddress {
		parsed, err := netip.ParsePrefix(prefix)
		if err != nil {
			return nil, E.Cause(err, "amneziawg local_address")
		}
		local = append(local, parsed.Addr())
	}
	if len(local) == 0 {
		return nil, E.New("amneziawg: missing local_address")
	}
	mtu := options.MTU
	if mtu == 0 {
		mtu = 1280
	}
	// DNS внутри туннеля — для имён, которые приходят без адреса (режим «Браузеры и программы»).
	tunDevice, stack, err := netstack.CreateNetTUN(local, []netip.Addr{netip.MustParseAddr("1.1.1.1"), netip.MustParseAddr("8.8.8.8")}, mtu)
	if err != nil {
		return nil, E.Cause(err, "amneziawg tun")
	}
	systemDialer, err := dialer.New(ctx, option.DialerOptions{}, false)
	if err != nil {
		return nil, err
	}
	bind := &packetBind{ctx: ctx, dialer: systemDialer, server: M.ParseSocksaddrHostPort(options.Server, options.ServerPort)}
	wg := device.NewDevice(tunDevice, bind, device.NewLogger(device.LogLevelError, "amneziawg: "))
	if err := wg.IpcSet(uapi); err != nil {
		wg.Close()
		return nil, E.Cause(err, "amneziawg setup")
	}
	return &AmneziaWGOutbound{
		Adapter: outbound.NewAdapter(TypeAmneziaWG, tag, []string{N.NetworkTCP, N.NetworkUDP}, nil),
		logger:  logger, keepalive: amneziaKeepalive(options), device: wg, stack: stack, bind: bind,
	}, nil
}

// Строки UAPI устройства. Значения проверяются: перевод строки в них дописал бы чужую настройку.
func amneziaUAPI(options AmneziaWGOptions) (string, error) {
	key := func(name, value string) (string, error) {
		raw, err := base64.StdEncoding.DecodeString(value)
		if err != nil || len(raw) != 32 {
			return "", E.New("amneziawg: bad ", name)
		}
		return hex.EncodeToString(raw), nil
	}
	private, err := key("private_key", options.PrivateKey)
	if err != nil {
		return "", err
	}
	public, err := key("peer_public_key", options.PeerPublicKey)
	if err != nil {
		return "", err
	}
	if options.Server == "" || options.ServerPort == 0 {
		return "", E.New("amneziawg: missing server")
	}
	var b strings.Builder
	b.WriteString("private_key=" + private + "\n")
	for name, value := range options.Obfuscation {
		if !amneziaKeys[name] || value == "" || len(value) > 4096 || strings.ContainsAny(value, "\r\n=") {
			return "", E.New("amneziawg: bad obfuscation parameter ", name)
		}
		b.WriteString(name + "=" + value + "\n")
	}
	b.WriteString("public_key=" + public + "\n")
	if options.PreSharedKey != "" {
		psk, err := key("pre_shared_key", options.PreSharedKey)
		if err != nil {
			return "", err
		}
		b.WriteString("preshared_key=" + psk + "\n")
	}
	// Адрес сервера для устройства условный: пакеты всё равно уходят через packetBind на настоящий.
	b.WriteString("endpoint=" + amneziaEndpoint + "\n")
	b.WriteString("allowed_ip=0.0.0.0/0\nallowed_ip=::/0\n")
	return b.String(), nil
}

// Keepalive — отдельно и только при запуске выхода: заданный при настройке, он сразу шлёт пакет
// серверу, и проверка конфигурации (oblachka-core check) ждала бы DNS и сеть.
func amneziaKeepalive(options AmneziaWGOptions) string {
	if options.Keepalive <= 0 {
		return ""
	}
	raw, _ := base64.StdEncoding.DecodeString(options.PeerPublicKey)
	return "public_key=" + hex.EncodeToString(raw) + "\nupdate_only=true\npersistent_keepalive_interval=" + strconv.Itoa(options.Keepalive) + "\n"
}

func (h *AmneziaWGOutbound) Start(stage adapter.StartStage, scope *adapter.Scope) error {
	if stage != adapter.StartStateStart {
		return nil
	}
	if err := h.device.Up(); err != nil {
		return E.Cause(err, "amneziawg up")
	}
	if h.keepalive != "" {
		if err := h.device.IpcSet(h.keepalive); err != nil {
			return E.Cause(err, "amneziawg keepalive")
		}
	}
	scope.Add(func() error { h.device.Close(); return nil })
	return nil
}

func (h *AmneziaWGOutbound) DialContext(ctx context.Context, network string, destination M.Socksaddr) (net.Conn, error) {
	ctx, metadata := adapter.ExtendContext(ctx)
	metadata.Outbound = h.Tag()
	metadata.Destination = destination
	address, err := h.resolve(destination)
	if err != nil {
		return nil, err
	}
	switch N.NetworkName(network) {
	case N.NetworkTCP:
		h.logger.InfoContext(ctx, "outbound connection to ", destination)
		return h.stack.DialContextTCPAddrPort(ctx, address)
	case N.NetworkUDP:
		h.logger.InfoContext(ctx, "outbound packet connection to ", destination)
		return h.stack.DialUDPAddrPort(netip.AddrPort{}, address)
	default:
		return nil, E.Extend(N.ErrUnknownNetwork, network)
	}
}

func (h *AmneziaWGOutbound) ListenPacket(ctx context.Context, destination M.Socksaddr) (net.PacketConn, error) {
	ctx, metadata := adapter.ExtendContext(ctx)
	metadata.Outbound = h.Tag()
	metadata.Destination = destination
	h.logger.InfoContext(ctx, "outbound packet connection to ", destination)
	packetConn, err := h.stack.ListenUDPAddrPort(netip.AddrPort{})
	if err != nil {
		return nil, err
	}
	return packetConn, nil
}

func (h *AmneziaWGOutbound) resolve(destination M.Socksaddr) (netip.AddrPort, error) {
	if !destination.IsFqdn() {
		return netip.AddrPortFrom(destination.Addr.Unmap(), destination.Port), nil
	}
	addresses, err := h.stack.LookupHost(destination.Fqdn)
	if err != nil {
		return netip.AddrPort{}, err
	}
	for _, text := range addresses {
		if addr, err := netip.ParseAddr(text); err == nil {
			return netip.AddrPortFrom(addr.Unmap(), destination.Port), nil
		}
	}
	return netip.AddrPort{}, E.New("amneziawg: no address for ", destination.Fqdn)
}

// Связь устройства с сервером: один UDP-сокет к одному серверу, открытый набирателем sing-box.
// Устройство считает адресом пира amneziaEndpoint, а пакеты отсюда всегда уходят на server.
const amneziaEndpoint = "192.0.2.1:51820"

type packetBind struct {
	ctx    context.Context
	dialer N.Dialer
	server M.Socksaddr
	mu     sync.Mutex
	conn   net.PacketConn
	target net.Addr
}

type bindEndpoint struct{ addr netip.AddrPort }

func (e bindEndpoint) ClearSrc()           {}
func (e bindEndpoint) SrcToString() string { return "" }
func (e bindEndpoint) DstToString() string { return e.addr.String() }
func (e bindEndpoint) DstToBytes() []byte  { b, _ := e.addr.MarshalBinary(); return b }
func (e bindEndpoint) DstIP() netip.Addr   { return e.addr.Addr() }
func (e bindEndpoint) SrcIP() netip.Addr   { return netip.Addr{} }

func (b *packetBind) Open(uint16) ([]conn.ReceiveFunc, uint16, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.conn != nil {
		return nil, 0, conn.ErrBindAlreadyOpen
	}
	// Имя сервера разрешается при первой отправке, а не здесь: Open вызывается и при проверке
	// конфигурации, и нерешаемое имя держало бы её десятки секунд. В «Весь компьютер» сервер
	// приходит уже адресом (план туннеля разрешает имена заранее).
	listen := b.server
	if listen.IsFqdn() {
		listen = M.SocksaddrFrom(netip.IPv4Unspecified(), b.server.Port)
	}
	packetConn, err := b.dialer.ListenPacket(b.ctx, listen)
	if err != nil {
		return nil, 0, err
	}
	b.conn, b.target = packetConn, nil
	if !b.server.IsFqdn() {
		b.target = b.server.UDPAddr()
	}
	endpoint := bindEndpoint{netip.MustParseAddrPort(amneziaEndpoint)}
	receive := func(packets [][]byte, sizes []int, eps []conn.Endpoint) (int, error) {
		n, _, err := packetConn.ReadFrom(packets[0])
		if err != nil {
			return 0, err
		}
		sizes[0], eps[0] = n, endpoint
		return 1, nil
	}
	return []conn.ReceiveFunc{receive}, 0, nil
}

func (b *packetBind) Close() error {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.conn == nil {
		return nil
	}
	err := b.conn.Close()
	b.conn = nil
	return err
}

func (b *packetBind) Send(bufs [][]byte, _ conn.Endpoint) error {
	b.mu.Lock()
	packetConn, target := b.conn, b.target
	b.mu.Unlock()
	if packetConn == nil {
		return net.ErrClosed
	}
	if target == nil {
		resolved, err := net.ResolveUDPAddr("udp4", b.server.String())
		if err != nil {
			return err
		}
		b.mu.Lock()
		b.target, target = resolved, resolved
		b.mu.Unlock()
	}
	for _, buf := range bufs {
		if _, err := packetConn.WriteTo(buf, target); err != nil {
			return err
		}
	}
	return nil
}

func (b *packetBind) ParseEndpoint(s string) (conn.Endpoint, error) {
	addr, err := netip.ParseAddrPort(s)
	if err != nil {
		return nil, err
	}
	return bindEndpoint{addr}, nil
}

func (b *packetBind) SetMark(uint32) error { return nil }
func (b *packetBind) BatchSize() int       { return 1 }
