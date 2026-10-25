#ifndef EXTEND_H
#define EXTEND_H

#include <stddef.h>

#include "proxy.h"

int socket_mod(int fd, struct sockaddr *dst);

int bind_out_if(int fd, int family, int need6);

int connect_hook(struct poolhd *pool, struct eval *val, 
        struct sockaddr_ina *dst, int next);
        
int on_tunnel_check(struct poolhd *pool, struct eval *val,
        char *buffer, size_t bfsize, int out);

int on_desync(struct poolhd *pool, struct eval *val,
        char *buffer, size_t bfsize, int out);

ssize_t udp_hook(struct eval *val, 
        char *buffer, size_t bfsize, ssize_t n, struct sockaddr_ina *dst);

#ifdef __linux__
int protect(int conn_fd, const char *path);
#else
#define protect(fd, path) 0
#endif

#endif
