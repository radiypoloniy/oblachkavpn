// oblachka-core — ядро системного туннеля Облачки: sing-box 1.14.4, выход "xray"
// (встроенный Xray для XHTTP и других протоколов, которых sing-box не умеет) и выход "amneziawg".
//
// Командная строка совпадает с той частью sing-box, которой пользуется служба:
//
//	oblachka-core run -c stdin
//	oblachka-core check -c stdin
//	oblachka-core version
package main

import (
	"context"
	"fmt"
	"io"
	"os"
	"os/signal"
	runtimeDebug "runtime/debug"
	"syscall"
	"time"

	box "github.com/sagernet/sing-box"
	C "github.com/sagernet/sing-box/constant"
	"github.com/sagernet/sing-box/experimental/deprecated"
	"github.com/sagernet/sing-box/include"
	"github.com/sagernet/sing-box/log"
	"github.com/sagernet/sing-box/option"
	E "github.com/sagernet/sing/common/exceptions"
	"github.com/sagernet/sing/common/json"
	"github.com/sagernet/sing/service"
)

func main() {
	if len(os.Args) == 2 && os.Args[1] == "version" {
		fmt.Println("oblachka-core: sing-box " + C.Version + ", xray and amneziawg outbounds")
		return
	}
	if len(os.Args) != 4 || os.Args[2] != "-c" || os.Args[3] != "stdin" || (os.Args[1] != "run" && os.Args[1] != "check") {
		fmt.Fprintln(os.Stderr, "usage: oblachka-core run|check -c stdin")
		os.Exit(2)
	}
	ctx := globalContext()
	options, err := readOptions(ctx)
	if err == nil {
		if os.Args[1] == "check" {
			err = check(ctx, options)
		} else {
			err = run(ctx, options)
		}
	}
	if err != nil {
		log.Fatal(err)
	}
}

func globalContext() context.Context {
	outbounds := include.OutboundRegistry()
	RegisterXrayOutbound(outbounds)
	RegisterAmneziaWGOutbound(outbounds)
	ctx := service.ContextWith(context.Background(), deprecated.NewStderrManager(log.StdLogger()))
	return box.Context(ctx, include.InboundRegistry(), outbounds, include.EndpointRegistry(),
		include.DNSTransportRegistry(), include.ServiceRegistry(), include.CertificateProviderRegistry())
}

func readOptions(ctx context.Context) (option.Options, error) {
	content, err := io.ReadAll(os.Stdin)
	if err != nil {
		return option.Options{}, E.Cause(err, "read config")
	}
	options, err := json.UnmarshalExtendedContext[option.Options](ctx, content)
	if err != nil {
		return option.Options{}, E.Cause(err, "decode config")
	}
	return options, nil
}

func check(ctx context.Context, options option.Options) error {
	ctx, cancel := context.WithCancel(service.ExtendContext(ctx))
	defer cancel()
	instance, err := box.New(box.Options{Context: ctx, Options: options})
	if err == nil {
		instance.Close()
	}
	return err
}

// Как sing-box run: остановка по Ctrl+C (служба шлёт его группе процессов ядра),
// чтобы ядро само убрало адаптер wintun.
func run(ctx context.Context, options option.Options) error {
	osSignals := make(chan os.Signal, 1)
	signal.Notify(osSignals, os.Interrupt, syscall.SIGTERM)
	defer signal.Stop(osSignals)
	ctx, cancel := context.WithCancel(service.ExtendContext(ctx))
	defer cancel()
	instance, err := box.New(box.Options{Context: ctx, Options: options})
	if err != nil {
		return E.Cause(err, "create service")
	}
	startCtx, finishStart := context.WithCancel(context.Background())
	go func() {
		if _, loaded := <-osSignals; loaded {
			cancel()
			closeMonitor(startCtx)
		}
	}()
	err = instance.Start()
	finishStart()
	if err != nil {
		instance.Close()
		return E.Cause(err, "start service")
	}
	runtimeDebug.FreeOSMemory()
	<-ctx.Done()
	closeCtx, closed := context.WithCancel(context.Background())
	go closeMonitor(closeCtx)
	err = instance.Close()
	closed()
	if err != nil {
		log.Error(E.Cause(err, "core did not close properly"))
	}
	return nil
}

func closeMonitor(ctx context.Context) {
	time.Sleep(C.FatalStopTimeout)
	select {
	case <-ctx.Done():
		return
	default:
	}
	log.Fatal("core did not close!")
}
