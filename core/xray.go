package main

// Выход sing-box типа "xray": соединение отдаётся встроенному Xray прямо в памяти
// (core.Dial — канал внутри процесса), без SOCKS на 127.0.0.1 и второго процесса.
// Так ядро туннеля службы несёт протоколы, которых sing-box не умеет (XHTTP и др.).
//
// Конфигурацию выхода служба уже проверила (DirectOutboundPolicy, белый список полей);
// здесь она ещё раз разбирается строго — неизвестное поле Xray отклоняет.

import (
	"bytes"
	"context"
	"encoding/json"
	"net"

	"github.com/sagernet/sing-box/adapter"
	"github.com/sagernet/sing-box/adapter/outbound"
	"github.com/sagernet/sing-box/common/dialer"
	"github.com/sagernet/sing-box/log"
	"github.com/sagernet/sing-box/option"
	E "github.com/sagernet/sing/common/exceptions"
	M "github.com/sagernet/sing/common/metadata"
	N "github.com/sagernet/sing/common/network"

	xnet "github.com/xtls/xray-core/common/net"
	xcore "github.com/xtls/xray-core/core"
	"github.com/xtls/xray-core/infra/conf/serial"
	"github.com/xtls/xray-core/transport/internet"
)

const TypeXray = "xray"

type XrayOutboundOptions struct {
	// Выход Xray в его собственном формате (элемент "outbounds" конфигурации Xray).
	Xray json.RawMessage `json:"xray"`
}

type XrayOutbound struct {
	outbound.Adapter
	logger   log.ContextLogger
	instance *xcore.Instance
}

func RegisterXrayOutbound(registry *outbound.Registry) {
	outbound.Register[XrayOutboundOptions](registry, TypeXray, NewXrayOutbound)
}

func NewXrayOutbound(ctx context.Context, router adapter.Router, logger log.ContextLogger, tag string, options XrayOutboundOptions) (adapter.Outbound, error) {
	if len(options.Xray) == 0 {
		return nil, E.New("missing xray outbound")
	}
	var raw bytes.Buffer
	raw.WriteString(`{"log":{"loglevel":"warning","access":"none","dnsLog":false},"outbounds":[`)
	raw.Write(options.Xray)
	raw.WriteString(`]}`)
	decoded, err := serial.DecodeJSONConfigStrict(&raw)
	if err != nil {
		return nil, E.Cause(err, "decode xray outbound")
	}
	config, err := decoded.Build()
	if err != nil {
		return nil, E.Cause(err, "build xray outbound")
	}
	if len(config.Outbound) != 1 || len(config.Inbound) != 0 {
		return nil, E.New("xray outbound: exactly one outbound expected")
	}
	// Сокеты Xray открываются тем же набирателем, что и выходы sing-box: с привязкой
	// к физическому интерфейсу (auto_detect_interface), иначе они ушли бы обратно в TUN.
	systemDialer, err := dialer.New(ctx, option.DialerOptions{}, false)
	if err != nil {
		return nil, err
	}
	useSystemDialer(systemDialer)
	instance, err := xcore.New(config)
	if err != nil {
		return nil, E.Cause(err, "create xray outbound")
	}
	return &XrayOutbound{
		Adapter:  outbound.NewAdapter(TypeXray, tag, []string{N.NetworkTCP, N.NetworkUDP}, nil),
		logger:   logger,
		instance: instance,
	}, nil
}

func (h *XrayOutbound) Start(stage adapter.StartStage, scope *adapter.Scope) error {
	if stage != adapter.StartStateStart {
		return nil
	}
	if err := h.instance.Start(); err != nil {
		return err
	}
	scope.Add(h.instance.Close)
	return nil
}

func (h *XrayOutbound) DialContext(ctx context.Context, network string, destination M.Socksaddr) (net.Conn, error) {
	ctx, metadata := adapter.ExtendContext(ctx)
	metadata.Outbound = h.Tag()
	metadata.Destination = destination
	switch N.NetworkName(network) {
	case N.NetworkTCP:
		h.logger.InfoContext(ctx, "outbound connection to ", destination)
		return xcore.Dial(ctx, h.instance, xnet.TCPDestination(address(destination), xnet.Port(destination.Port)))
	case N.NetworkUDP:
		h.logger.InfoContext(ctx, "outbound packet connection to ", destination)
		return xcore.Dial(ctx, h.instance, xnet.UDPDestination(address(destination), xnet.Port(destination.Port)))
	default:
		return nil, E.Extend(N.ErrUnknownNetwork, network)
	}
}

func (h *XrayOutbound) ListenPacket(ctx context.Context, destination M.Socksaddr) (net.PacketConn, error) {
	ctx, metadata := adapter.ExtendContext(ctx)
	metadata.Outbound = h.Tag()
	metadata.Destination = destination
	h.logger.InfoContext(ctx, "outbound packet connection to ", destination)
	return xcore.DialUDP(ctx, h.instance)
}

func address(destination M.Socksaddr) xnet.Address {
	if destination.IsFqdn() {
		return xnet.DomainAddress(destination.Fqdn)
	}
	return xnet.IPAddress(destination.Addr.AsSlice())
}

// Набиратель Xray поверх набирателя sing-box. Он общий для процесса: в ядре одна
// конфигурация sing-box, и у всех её выходов одинаковая привязка к интерфейсу.
type boxSystemDialer struct{ dialer N.Dialer }

func useSystemDialer(d N.Dialer) { internet.UseAlternativeSystemDialer(&boxSystemDialer{d}) }

func (d *boxSystemDialer) Dial(ctx context.Context, _ xnet.Address, destination xnet.Destination, _ *internet.SocketConfig) (net.Conn, error) {
	target := M.ParseSocksaddrHostPort(destination.Address.String(), uint16(destination.Port))
	if destination.Network == xnet.Network_UDP {
		packetConn, err := d.dialer.ListenPacket(ctx, target)
		if err != nil {
			return nil, err
		}
		return &internet.PacketConnWrapper{PacketConn: packetConn, Dest: target.UDPAddr()}, nil
	}
	return d.dialer.DialContext(ctx, N.NetworkTCP, target)
}

func (d *boxSystemDialer) DestIpAddress() net.IP { return nil }
